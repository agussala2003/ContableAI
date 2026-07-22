using ContableAI.Domain.Common;
using ContableAI.Domain.Constants;
using ContableAI.Domain.Entities;
using ContableAI.Domain.Enums;
using ContableAI.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ContableAI.Infrastructure.BackgroundJobs;

/// <summary>
/// R-5: análisis de aprendizaje proactivo, ejecutado como <b>job recurrente de Hangfire</b>
/// (una vez al día). Reemplaza al antiguo <c>BackgroundService</c> con <c>Task.Delay(24h)</c>,
/// que corría en-proceso en <b>cada</b> réplica (trabajo duplicado + race al insertar sugerencias)
/// y cuyo timer se reiniciaba en cada redeploy (podía no dispararse nunca).
///
/// Hangfire garantiza corrida única mediante lock distribuido sobre el storage de PostgreSQL,
/// scheduling durable y reintentos — apto para múltiples contenedores efímeros.
///
/// Hangfire resuelve esta clase desde el contenedor de DI y crea un scope por ejecución,
/// por lo que el <see cref="ContableAIDbContext"/> (scoped) se inyecta directamente.
/// </summary>
public class ProactiveLearningJob
{
    /// <summary>Identificador estable del recurring job (usado por <c>AddOrUpdate</c>).</summary>
    public const string RecurringJobId = "proactive-learning";

    private readonly ContableAIDbContext _db;
    private readonly ILogger<ProactiveLearningJob> _logger;

    public ProactiveLearningJob(ContableAIDbContext db, ILogger<ProactiveLearningJob> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Detecta grupos de transacciones clasificadas manualmente con el mismo keyword+cuenta y
    /// genera sugerencias de reglas. Idempotente: verifica existencia de regla/sugerencia antes
    /// de insertar, por lo que reejecutarlo no duplica datos.
    ///
    /// P-7: procesa EMPRESA POR EMPRESA — antes materializaba 2 años de movimientos manuales de
    /// TODOS los estudios en una sola query (memoria proporcional a la plataforma entera). Ahora
    /// el pico de memoria queda acotado al volumen de una empresa, y los chequeos de reglas/
    /// sugerencias van batcheados por empresa (2 queries por empresa, no 2 por grupo).
    /// </summary>
    public async Task AnalyzeTransactionsAsync(CancellationToken ct = default)
    {
        var cutoffDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-730));

        // Solo los IDs de empresas con actividad manual en la ventana: es el conjunto de
        // trabajo real, resuelto como DISTINCT en Postgres.
        var companyIds = await _db.BankTransactions
            .Where(t => t.ClassificationSource == ClassificationSources.Manual
                     && t.AssignedAccount != null
                     && t.CompanyId != null
                     && t.Date >= cutoffDate)
            .Select(t => t.CompanyId!.Value)
            .Distinct()
            .ToListAsync(ct);

        int newSuggestions = 0;
        foreach (var companyId in companyIds)
        {
            ct.ThrowIfCancellationRequested();
            newSuggestions += await AnalyzeCompanyAsync(companyId, cutoffDate, ct);
        }

        if (newSuggestions > 0)
            _logger.LogInformation("ProactiveLearningJob generó {Count} nuevas sugerencias de reglas.", newSuggestions);
    }

    /// <summary>Analiza una sola empresa; devuelve la cantidad de sugerencias nuevas creadas.</summary>
    private async Task<int> AnalyzeCompanyAsync(Guid companyId, DateOnly cutoffDate, CancellationToken ct)
    {
        var raw = await _db.BankTransactions
            .Where(t => t.CompanyId == companyId
                     && t.ClassificationSource == ClassificationSources.Manual
                     && t.AssignedAccount != null
                     && t.Date >= cutoffDate)
            .Select(t => new { t.TenantId, t.Description, Account = t.AssignedAccount })
            .ToListAsync(ct);

        // La normalización de keywords no es traducible a SQL: se agrupa en memoria, pero
        // ahora sobre los movimientos de UNA empresa, no de toda la plataforma.
        var candidateGroups = raw
            .GroupBy(t => new { Keyword = KeywordNormalizer.Normalize(t.Description), t.Account })
            .Where(g => !string.IsNullOrWhiteSpace(g.Key.Keyword) && g.Key.Account != null && g.Count() >= 3)
            .Select(g => new
            {
                g.Key.Keyword,
                Account  = g.Key.Account!,
                Count    = g.Count(),
                TenantId = g.First().TenantId,
            })
            .ToList();

        if (candidateGroups.Count == 0) return 0;

        var keywords = candidateGroups.Select(g => g.Keyword).Distinct().ToList();

        // Batch por empresa: 1 query de reglas + 1 de sugerencias para todos los keywords.
        var existingRules = (await _db.AccountingRules
                .Where(r => r.CompanyId == companyId && keywords.Contains(r.Keyword))
                .Select(r => r.Keyword)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        var existingSuggestions = (await _db.RuleSuggestions
                .Where(s => s.CompanyId == companyId && keywords.Contains(s.Keyword))
                .ToListAsync(ct))
            .ToDictionary(s => s.Keyword, StringComparer.Ordinal);

        int newSuggestions = 0;
        bool dirty = false;

        foreach (var group in candidateGroups)
        {
            if (existingRules.Contains(group.Keyword)) continue;

            if (existingSuggestions.TryGetValue(group.Keyword, out var existing))
            {
                // Si la frecuencia aumentó y sigue pendiente, actualizarla. Las Rejected no se
                // reactivan desde el job (se respeta la decisión del usuario; solo una nueva
                // asignación manual las reactiva, ver bulk-update).
                if (existing.Status == SuggestionStatus.Pending && existing.Frequency < group.Count)
                {
                    existing.Frequency = group.Count;
                    dirty = true;
                }
                continue;
            }

            _db.RuleSuggestions.Add(new RuleSuggestion
            {
                TenantId         = group.TenantId,
                CompanyId        = companyId,
                Keyword          = group.Keyword,
                SuggestedAccount = group.Account,
                Frequency        = group.Count,
                Status           = SuggestionStatus.Pending
            });
            newSuggestions++;
            dirty = true;
        }

        if (dirty)
            await _db.SaveChangesAsync(ct);

        return newSuggestions;
    }
}
