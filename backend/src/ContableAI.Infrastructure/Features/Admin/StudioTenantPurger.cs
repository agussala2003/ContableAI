using ContableAI.Domain.Entities;
using ContableAI.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ContableAI.Infrastructure.Features.Admin;

/// <summary>
/// Cascada de borrado/seudonimización de TODOS los datos de un estudio (tenant). Es la única
/// implementación de la limpieza: la usan tanto <see cref="DeleteStudioTenantHandler"/> (cierre
/// formal de cuenta, P-1) como <see cref="DeleteUserHandler"/> cuando cae el último usuario del
/// estudio (P-3), para que ninguno de los dos caminos deje residuos.
///
/// Orden de borrado: hijos antes que padres (líneas → asientos → transacciones → empresas),
/// porque los deletes set-based no disparan cascadas del change tracker.
/// </summary>
internal static class StudioTenantPurger
{
    /// <summary>
    /// Un archivo staged es huérfano si su job de Hangfire nunca lo consumió. Los jobs corren en
    /// segundos/minutos; a las 24 h ya no hay job vivo que pueda reclamarlo (P-5, red de seguridad).
    /// </summary>
    private static readonly TimeSpan StagedFileOrphanAge = TimeSpan.FromHours(24);

    internal sealed record PurgeCounts(
        int Users, int RefreshTokens, int Companies, int BankTransactions,
        int JournalEntries, int JournalEntryLines, int AccountingRules, int RuleSuggestions,
        int AfipVouchers, int ChartOfAccounts, int ClosedPeriods, int UploadJobResults,
        int StagedFilesPurged, int AuditLogsAnonymized);

    internal sealed record CompanyPurgeCounts(
        int Companies, int BankTransactions, int JournalEntries, int JournalEntryLines,
        int AccountingRules, int RuleSuggestions, int AfipVouchers);

    /// <summary>
    /// Cascada de borrado de un conjunto de empresas y todos sus datos (hijos → padres).
    /// La usan el cierre de tenant (<see cref="PurgeAsync"/>) y el <c>DataRetentionJob</c>
    /// para el hard-delete diferido de empresas soft-deleted (P-2: DeletedAt vencido).
    /// No toca datos a nivel estudio (reglas de estudio, plan de cuentas, usuarios).
    /// </summary>
    internal static async Task<CompanyPurgeCounts> PurgeCompaniesAsync(
        ContableAIDbContext db, IReadOnlyCollection<Guid> companyIds, CancellationToken ct)
    {
        if (companyIds.Count == 0)
            return new CompanyPurgeCounts(0, 0, 0, 0, 0, 0, 0);

        var jeIds = await db.JournalEntries
            .Where(je => je.CompanyId != null && companyIds.Contains(je.CompanyId.Value))
            .Select(je => je.Id)
            .ToListAsync(ct);

        var lines = await DeleteAsync(db,
            db.JournalEntryLines.Where(l => jeIds.Contains(l.JournalEntryId)), ct);

        var entries = await DeleteAsync(db,
            db.JournalEntries.Where(je => je.CompanyId != null && companyIds.Contains(je.CompanyId.Value)), ct);

        var txs = await DeleteAsync(db,
            db.BankTransactions.IgnoreQueryFilters()
                .Where(t => t.CompanyId != null && companyIds.Contains(t.CompanyId.Value)), ct);

        var vouchers = await DeleteAsync(db,
            db.AfipVouchers.Where(v => companyIds.Contains(v.CompanyId)), ct);

        var rules = await DeleteAsync(db,
            db.AccountingRules.IgnoreQueryFilters()
                .Where(r => r.CompanyId != null && companyIds.Contains(r.CompanyId.Value)), ct);

        var suggestions = await DeleteAsync(db,
            db.RuleSuggestions.Where(s => s.CompanyId != null && companyIds.Contains(s.CompanyId.Value)), ct);

        var companies = await DeleteAsync(db,
            db.Companies.IgnoreQueryFilters().Where(c => companyIds.Contains(c.Id)), ct);

        return new CompanyPurgeCounts(companies, txs, entries, lines, rules, suggestions, vouchers);
    }

    /// <summary>
    /// Elimina todos los datos del tenant y seudonimiza sus <see cref="AuditLog"/>. No abre
    /// transacción ni escribe el AuditLog de cierre: eso es responsabilidad del caller.
    /// Usa <c>IgnoreQueryFilters()</c> para no depender del tenant del contexto que la invoca.
    /// </summary>
    internal static async Task<PurgeCounts> PurgeAsync(
        ContableAIDbContext db, string studioTenantId, CancellationToken ct)
    {
        // IDs base del tenant. Empresas: TODAS, incluidas las soft-deleted (IsActive = false).
        var companyIds = await db.Companies.IgnoreQueryFilters()
            .Where(c => c.StudioTenantId == studioTenantId)
            .Select(c => c.Id)
            .ToListAsync(ct);

        var userIds = await db.Users
            .Where(u => u.StudioTenantId == studioTenantId)
            .Select(u => u.Id)
            .ToListAsync(ct);

        // El plan de cuentas a nivel estudio guarda el tenant como Guid (AccountingRule ya no:
        // desde el hardening de reglas lo guarda como string, igual que Company).
        Guid? studioGuid = Guid.TryParse(studioTenantId, out var g) ? g : null;

        // ── Datos por empresa (hijos → padres) — cascada compartida ──────────
        var companyCounts = await PurgeCompaniesAsync(db, companyIds, ct);

        // P-2: residuos con el estudio estampado pero sin empresa (CompanyId quedó null por el
        // FK SetNull de bajas anteriores). Antes eran inalcanzables; la columna desnormalizada
        // permite barrerlos en el cierre de cuenta.
        var orphanTxs = await DeleteAsync(db,
            db.BankTransactions.IgnoreQueryFilters().Where(t => t.StudioTenantId == studioTenantId), ct);

        // ── Datos a nivel estudio ─────────────────────────────────────────────
        // Reglas de estudio (CompanyId null + StudioTenantId del tenant) — P-3.
        // IgnoreQueryFilters: la purga corre desde el cierre de cuenta y desde el job de retención,
        // que no tienen el tenant del contexto seteado; no debe depender del filtro global.
        var studioRules = await DeleteAsync(db,
            db.AccountingRules.IgnoreQueryFilters().Where(r =>
                r.CompanyId == null && r.StudioTenantId == studioTenantId), ct);

        // Sugerencias residuales ancladas solo por TenantId (las de empresa ya cayeron arriba).
        var tenantSuggestions = await DeleteAsync(db,
            db.RuleSuggestions.Where(s => s.TenantId == studioTenantId), ct);

        var accounts = studioGuid is null ? 0 : await DeleteAsync(db,
            db.ChartOfAccounts.Where(a => a.StudioTenantId == studioGuid), ct);

        var periods = await DeleteAsync(db,
            db.ClosedPeriods.Where(p => p.StudioTenantId == studioTenantId), ct);

        // Residuos del pipeline asíncrono — P-3: resultados de jobs con datos financieros.
        var jobResults = await DeleteAsync(db,
            db.UploadJobResults.Where(r => r.StudioTenantId == studioTenantId), ct);

        // StagedUploadFiles no tiene columna de tenant: se purgan los huérfanos por edad (P-5).
        var stagedCutoff = DateTime.UtcNow - StagedFileOrphanAge;
        var staged = await DeleteAsync(db,
            db.StagedUploadFiles.Where(f => f.CreatedAt < stagedCutoff), ct);

        // Sesiones: sin esto los RefreshTokens quedaban huérfanos para siempre (P-3).
        var tokens = await DeleteAsync(db,
            db.RefreshTokens.Where(t => userIds.Contains(t.UserId)), ct);

        var users = await DeleteAsync(db,
            db.Users.Where(u => u.StudioTenantId == studioTenantId), ct);

        var anonymized = await AnonymizeAuditLogsAsync(db, studioTenantId,
            userIds.Select(id => id.ToString()).ToList(), ct);

        return new PurgeCounts(
            users, tokens, companyCounts.Companies, companyCounts.BankTransactions + orphanTxs,
            companyCounts.JournalEntries, companyCounts.JournalEntryLines,
            companyCounts.AccountingRules + studioRules,
            companyCounts.RuleSuggestions + tenantSuggestions,
            companyCounts.AfipVouchers, accounts, periods, jobResults, staged, anonymized);
    }

    /// <summary>
    /// Seudonimiza los AuditLogs de un tenant (o de usuarios puntuales): conserva la fila —
    /// trazabilidad financiera legal, P-6 — pero reemplaza el email por
    /// <c>deleted-user-{userId}@anonymized.local</c> y vacía el diff (<c>Changes</c>), que puede
    /// contener descripciones de movimientos, importes y CUITs.
    /// </summary>
    internal static async Task<int> AnonymizeAuditLogsAsync(
        ContableAIDbContext db, string? studioTenantId, IReadOnlyCollection<string> userIds, CancellationToken ct)
    {
        var query = db.AuditLogs.Where(a =>
            (studioTenantId != null && a.TenantId == studioTenantId) || userIds.Contains(a.UserId));

        // Idempotencia: no re-tocar filas ya anonimizadas (permite reintentos del comando).
        query = query.Where(a => !a.UserEmail.EndsWith("@anonymized.local"));

        if (db.Database.IsRelational())
        {
            return await query.ExecuteUpdateAsync(s => s
                .SetProperty(a => a.UserEmail, a => "deleted-user-" + a.UserId + "@anonymized.local")
                .SetProperty(a => a.Changes, (string?)null), ct);
        }

        // Proveedor InMemory (tests): mismo efecto, evaluado en cliente.
        var rows = await query.ToListAsync(ct);
        foreach (var log in rows)
        {
            log.UserEmail = $"deleted-user-{log.UserId}@anonymized.local";
            log.Changes   = null;
        }
        await db.SaveChangesAsync(ct);
        return rows.Count;
    }

    /// <summary>
    /// Delete set-based (<c>ExecuteDeleteAsync</c>, sin materializar filas — P-10) en el proveedor
    /// relacional; el proveedor InMemory de los tests no lo soporta y cae a RemoveRange.
    /// </summary>
    private static async Task<int> DeleteAsync<TEntity>(
        ContableAIDbContext db, IQueryable<TEntity> query, CancellationToken ct) where TEntity : class
    {
        if (db.Database.IsRelational())
            return await query.ExecuteDeleteAsync(ct);

        var rows = await query.ToListAsync(ct);
        db.RemoveRange(rows);
        await db.SaveChangesAsync(ct);
        return rows.Count;
    }
}
