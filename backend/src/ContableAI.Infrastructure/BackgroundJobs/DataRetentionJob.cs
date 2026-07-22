using ContableAI.Infrastructure.Features.Admin;
using ContableAI.Infrastructure.Options;
using ContableAI.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ContableAI.Infrastructure.BackgroundJobs;

/// <summary>
/// P-4/P-5/P-2: job recurrente diario de Hangfire que aplica la política de retención de datos
/// (documentada en <c>docs/RETENCION_DATOS.md</c>):
///
///   1. <b>UploadJobResults</b> (P-4): el JSON de resultado de cada subida se consume por
///      polling apenas termina el job; pasados <see cref="DataRetentionOptions.UploadJobResultsDays"/>
///      días es solo acumulación de datos financieros de staging → se borra.
///   2. <b>StagedUploadFiles</b> (P-5): red de seguridad para archivos cuyo job de Hangfire
///      nunca ejecutó (reintentos agotados, deploy a mitad de cola). El flujo normal borra la
///      fila al leer los bytes; lo que sobreviva más de
///      <see cref="DataRetentionOptions.StagedFileOrphanHours"/> horas es huérfano.
///   3. <b>Companies soft-deleted</b> (P-2, purga diferida): empresas con
///      <c>DeletedAt</c> más viejo que <see cref="DataRetentionOptions.SoftDeletedCompanyDays"/>
///      días se hard-deletean en cascada (misma cascada por empresa del cierre de cuenta,
///      <see cref="StudioTenantPurger.PurgeCompaniesAsync"/>).
///
/// Mismo patrón operativo que <see cref="ProactiveLearningJob"/> (R-5): Hangfire garantiza
/// corrida única vía lock distribuido sobre PostgreSQL y crea un scope de DI por ejecución
/// (el DbContext llega sin tenant → Global Query Filters desactivados, correcto para un job
/// de plataforma). Los borrados son set-based (<c>ExecuteDeleteAsync</c>, P-10): un solo
/// DELETE por tabla, sin materializar filas. Idempotente: reejecutarlo no tiene efecto
/// adicional hasta que nuevas filas venzan.
/// </summary>
public class DataRetentionJob
{
    /// <summary>Identificador estable del recurring job (usado por <c>AddOrUpdate</c>).</summary>
    public const string RecurringJobId = "data-retention";

    private readonly ContableAIDbContext _db;
    private readonly DataRetentionOptions _options;
    private readonly ILogger<DataRetentionJob> _logger;

    public DataRetentionJob(
        ContableAIDbContext db,
        IOptions<DataRetentionOptions> options,
        ILogger<DataRetentionJob> logger)
    {
        _db      = db;
        _options = options.Value;
        _logger  = logger;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        // ── P-4: resultados de jobs de subida vencidos ────────────────────────
        var resultsCutoff = now.AddDays(-_options.UploadJobResultsDays);
        var jobResults = await DeleteAsync(
            _db.UploadJobResults.Where(r => r.CreatedAt < resultsCutoff), ct);

        // ── P-5: archivos de staging huérfanos ────────────────────────────────
        var stagedCutoff = now.AddHours(-_options.StagedFileOrphanHours);
        var stagedFiles = await DeleteAsync(
            _db.StagedUploadFiles.Where(f => f.CreatedAt < stagedCutoff), ct);

        // ── P-2: purga diferida de empresas dadas de baja ─────────────────────
        var companyCutoff = now.AddDays(-_options.SoftDeletedCompanyDays);
        var expiredCompanyIds = await _db.Companies.IgnoreQueryFilters()
            .Where(c => !c.IsActive && c.DeletedAt != null && c.DeletedAt < companyCutoff)
            .Select(c => c.Id)
            .ToListAsync(ct);

        var companies = await StudioTenantPurger.PurgeCompaniesAsync(_db, expiredCompanyIds, ct);

        if (jobResults > 0 || stagedFiles > 0 || companies.Companies > 0)
        {
            _logger.LogInformation(
                "[RETENTION] Purga diaria — UploadJobResults={JobResults} (> {ResultsDays} días), " +
                "StagedUploadFiles huérfanos={Staged} (> {StagedHours} h), " +
                "Empresas hard-deleted={Companies} (baja > {CompanyDays} días; " +
                "transacciones={Txs}, asientos={Entries}, reglas={Rules})",
                jobResults, _options.UploadJobResultsDays,
                stagedFiles, _options.StagedFileOrphanHours,
                companies.Companies, _options.SoftDeletedCompanyDays,
                companies.BankTransactions, companies.JournalEntries, companies.AccountingRules);
        }
        else
        {
            _logger.LogDebug("[RETENTION] Purga diaria — nada que purgar.");
        }
    }

    /// <summary>Set-based en relacional (P-10); fallback RemoveRange para el proveedor InMemory de tests.</summary>
    private async Task<int> DeleteAsync<TEntity>(IQueryable<TEntity> query, CancellationToken ct)
        where TEntity : class
    {
        if (_db.Database.IsRelational())
            return await query.ExecuteDeleteAsync(ct);

        var rows = await query.ToListAsync(ct);
        _db.RemoveRange(rows);
        await _db.SaveChangesAsync(ct);
        return rows.Count;
    }
}
