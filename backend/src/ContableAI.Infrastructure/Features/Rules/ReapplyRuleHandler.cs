using ContableAI.Application.Common;
using ContableAI.Application.Features.Rules.Commands;
using ContableAI.Domain.Common;
using ContableAI.Domain.Constants;
using ContableAI.Domain.Entities;
using ContableAI.Domain.Enums;
using ContableAI.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace ContableAI.Infrastructure.Features.Rules;

/// <summary>
/// Ejecuta la reaplicación forzada de una regla. El mismo handler resuelve el preview
/// (<c>DryRun</c>) y la escritura: así el número que se le muestra al usuario sale exactamente
/// del mismo filtro que después modifica los datos.
///
/// Punto de entrada del job de Hangfire (encolado como <c>sender.Send(command)</c>, sin clase de
/// job dedicada — mismo patrón que <c>GenerateJournalEntriesCommandHandler</c>).
/// </summary>
public sealed class ReapplyRuleHandler : IRequestHandler<ReapplyRuleCommand, Result<ReapplyRuleReport>>
{
    /// <summary>Filas por sentencia UPDATE. Acota el tamaño del parámetro de ids y del lock.</summary>
    private const int BatchSize = 500;

    private const string ChequeTaxAccount = "IMPUESTO AL CHEQUE";

    private readonly ContableAIDbContext _db;
    private readonly ILogger<ReapplyRuleHandler> _logger;

    public ReapplyRuleHandler(ContableAIDbContext db, ILogger<ReapplyRuleHandler> logger)
    {
        _db     = db;
        _logger = logger;
    }

    public async Task<Result<ReapplyRuleReport>> Handle(ReapplyRuleCommand cmd, CancellationToken ct)
    {
        // IgnoreQueryFilters + chequeo explícito de estudio: como job de Hangfire no hay
        // HttpContext, así que el filtro global viene desactivado y el alcance lo tiene que
        // garantizar el propio handler a partir del tenant que viajó en el comando.
        var rule = await _db.AccountingRules.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == cmd.RuleId, ct);

        if (rule is null || rule.StudioTenantId != cmd.StudioTenantId)
            return Result<ReapplyRuleReport>.NotFound("Regla no encontrada.");

        // Decisión de alcance v1.1: la reaplicación retroactiva es solo para reglas de empresa.
        // Una regla de estudio abarca N empresas y su override masivo necesita otra UX.
        if (rule.CompanyId is null)
            return Result<ReapplyRuleReport>.Failure(
                "Solo se pueden reaplicar reglas propias de una empresa.", 422);

        var company = await _db.Companies.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == rule.CompanyId.Value, ct);

        if (company is null || company.StudioTenantId != cmd.StudioTenantId)
            return Result<ReapplyRuleReport>.NotFound("Empresa de la regla no encontrada.");

        var closedPeriods = (await _db.ClosedPeriods.IgnoreQueryFilters().AsNoTracking()
                .Where(p => p.StudioTenantId == cmd.StudioTenantId)
                .Select(p => new { p.Year, p.Month })
                .ToListAsync(ct))
            .Select(p => (p.Year, p.Month))
            .ToHashSet();

        // Prefiltro en la base por keyword + empresa + dirección. Las exclusiones (asentado,
        // período cerrado, cruce AFIP) se resuelven en memoria a propósito: hay que CONTARLAS
        // para el reporte, no solo descartarlas, y el conjunto ya viene acotado por el keyword.
        var pattern = KeywordMatcher.ToLikePattern(rule.Keyword);
        var direction = rule.Direction;

        var candidates = await _db.BankTransactions.IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.CompanyId == rule.CompanyId
                     && EF.Functions.ILike(t.Description, pattern, KeywordMatcher.LikeEscapeChar)
                     && (direction == null || t.Type == direction))
            .Select(t => new ReapplyCandidateClassifier.Candidate(
                t.Id, t.Description, t.Date, t.JournalEntryId,
                t.ClassificationSource, t.AssignedAccount, t.AppliedRuleId))
            .ToListAsync(ct);

        var targetSource = ResolveTargetSource(rule.TargetAccount, company.SplitChequeTax);

        var outcome = ReapplyCandidateClassifier.Classify(
            candidates, rule.Keyword, rule.TargetAccount, targetSource, rule.Id, closedPeriods,
            cmd.Scope);

        if (!cmd.DryRun && outcome.ToUpdate.Count > 0)
            await ApplyAsync(cmd, rule, targetSource, outcome, ct);

        var report = new ReapplyRuleReport(
            rule.Id, rule.Keyword, rule.TargetAccount, cmd.DryRun,
            outcome.ToUpdate.Count,
            outcome.Pending, outcome.ByOtherRule, outcome.Manual,
            outcome.SkippedSettled, outcome.SkippedClosedPeriod,
            outcome.SkippedAfipCombo, outcome.AlreadyApplied,
            outcome.SkippedOutOfScope);

        if (!cmd.DryRun)
            _logger.LogInformation(
                "Reaplicación forzada de la regla {RuleId} ({Keyword} → {Account}) por {User}: " +
                "{Updated} actualizados ({Pending} sin categorizar, {ByRule} por otra regla, {Manual} manuales); " +
                "omitidos {Settled} asentados, {Closed} en período cerrado, {Afip} de cruce AFIP",
                rule.Id, rule.Keyword, rule.TargetAccount, cmd.RequestedByEmail,
                report.TotalToUpdate, report.Pending, report.ByOtherRule, report.Manual,
                report.SkippedSettled, report.SkippedClosedPeriod, report.SkippedAfipCombo);

        return Result<ReapplyRuleReport>.Success(report);
    }

    /// <summary>
    /// Misma resolución de origen que el motor de clasificación: una regla de impuesto al cheque
    /// en una empresa con split marca el movimiento para que el asiento salga en dos líneas.
    /// </summary>
    private static string ResolveTargetSource(string targetAccount, bool splitChequeTax) =>
        splitChequeTax && targetAccount.Equals(ChequeTaxAccount, StringComparison.OrdinalIgnoreCase)
            ? ClassificationSources.ChequeTaxSplit
            : ClassificationSources.HardRule;

    /// <summary>
    /// Escribe el override en lotes y deja el registro de auditoría, todo en una transacción:
    /// una reaplicación a medias sin rastro de quién la disparó sería peor que no hacerla.
    ///
    /// Se usa <c>ExecuteUpdate</c> y no el change tracker para no materializar decenas de miles de
    /// entidades. La contrapartida es que NO pasa por el <c>AuditInterceptor</c> (que engancha en
    /// SaveChanges), así que el AuditLog se escribe explícitamente acá.
    /// </summary>
    private async Task ApplyAsync(
        ReapplyRuleCommand cmd, AccountingRule rule, string targetSource,
        ReapplyCandidateClassifier.Outcome outcome, CancellationToken ct)
    {
        var confidence = rule.RequiresTaxMatching ? 0.75f : 1.0f;

        // EnableRetryOnFailure (R-1) exige envolver la transacción explícita en la execution
        // strategy. En el proveedor InMemory (tests) no hay transacciones: se ejecuta directo.
        var strategy = _db.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = _db.Database.IsRelational()
                ? await _db.Database.BeginTransactionAsync(ct)
                : null;

            foreach (var chunk in outcome.ToUpdate.Chunk(BatchSize))
            {
                // OJO: `chunk` es Guid[], y sobre un array `Contains` liga a
                // MemoryExtensions.Contains(ReadOnlySpan<T>, T) —la sobrecarga de Span gana sobre
                // la de Enumerable—. EF Core no sabe traducir eso y el job explota en runtime con
                // "GenericArguments[1] 'System.ReadOnlySpan`1[System.Guid]' violates the constraint
                // of type 'TRet'". Materializarlo como List<Guid> fuerza Enumerable.Contains, que
                // sí se traduce a un `IN (...)`. No cambiar el tipo de esta variable.
                List<Guid> batch = [.. chunk];

                await _db.BankTransactions.IgnoreQueryFilters()
                    .Where(t => batch.Contains(t.Id))
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(t => t.AssignedAccount,      rule.TargetAccount)
                        .SetProperty(t => t.AppliedRuleId,        rule.Id)
                        .SetProperty(t => t.NeedsTaxMatching,     rule.RequiresTaxMatching)
                        .SetProperty(t => t.ClassificationSource, targetSource)
                        .SetProperty(t => t.ConfidenceScore,      confidence),
                        ct);
            }

            _db.AuditLogs.Add(new AuditLog
            {
                TenantId   = cmd.StudioTenantId,
                UserId     = "system",
                UserEmail  = cmd.RequestedByEmail,
                Action     = AuditAction.Updated.ToString(),
                EntityName = nameof(AccountingRule),
                EntityId   = rule.Id.ToString(),
                Changes    = JsonSerializer.Serialize(new
                {
                    Operation = "ReapplyRuleOverride",
                    rule.Keyword,
                    rule.TargetAccount,
                    Scope               = cmd.Scope.ToString(),
                    CompanyId           = rule.CompanyId,
                    Updated             = outcome.ToUpdate.Count,
                    FromPending         = outcome.Pending,
                    FromOtherRule       = outcome.ByOtherRule,
                    FromManual          = outcome.Manual,
                    SkippedSettled      = outcome.SkippedSettled,
                    SkippedClosedPeriod = outcome.SkippedClosedPeriod,
                    SkippedAfipCombo    = outcome.SkippedAfipCombo,
                }),
            });

            await _db.SaveChangesAsync(ct);

            if (tx is not null)
                await tx.CommitAsync(ct);
        });
    }
}
