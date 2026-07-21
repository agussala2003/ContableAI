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
    /// </summary>
    public async Task AnalyzeTransactionsAsync(CancellationToken ct = default)
    {
        var cutoffDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-730));

        // Fetch into memory so we can apply normalized keyword comparison
        var raw = await _db.BankTransactions
            .Where(t => t.ClassificationSource == ClassificationSources.Manual
                     && t.AssignedAccount != null
                     && t.Date >= cutoffDate)
            .Select(t => new { t.CompanyId, t.TenantId, t.Description, Account = t.AssignedAccount })
            .ToListAsync(ct);

        var candidateGroups = raw
            .GroupBy(t => new
            {
                t.CompanyId,
                t.TenantId,
                Keyword = KeywordNormalizer.Normalize(t.Description),
                Account = t.Account,
            })
            .Where(g => !string.IsNullOrWhiteSpace(g.Key.Keyword) && g.Count() >= 3)
            .Select(g => new
            {
                g.Key.CompanyId,
                g.Key.TenantId,
                g.Key.Keyword,
                g.Key.Account,
                Count = g.Count(),
            })
            .ToList();

        int newSuggestions = 0;

        foreach (var group in candidateGroups)
        {
            if (group.Account is null) continue;

            bool ruleExists = await _db.AccountingRules
                .AnyAsync(r => r.CompanyId == group.CompanyId && r.Keyword == group.Keyword, ct);

            if (ruleExists) continue;

            var existingSuggestion = await _db.RuleSuggestions
                .FirstOrDefaultAsync(s => s.CompanyId == group.CompanyId && s.Keyword == group.Keyword, ct);

            if (existingSuggestion != null)
            {
                // Si la frecuencia aumentó y sigue pendiente, actualizar la frecuencia
                if (existingSuggestion.Status == SuggestionStatus.Pending && existingSuggestion.Frequency < group.Count)
                {
                    existingSuggestion.Frequency = group.Count;
                }
                continue;
            }

            var suggestion = new RuleSuggestion
            {
                TenantId         = group.TenantId,
                CompanyId        = group.CompanyId,
                Keyword          = group.Keyword,
                SuggestedAccount = group.Account,
                Frequency        = group.Count,
                Status           = SuggestionStatus.Pending
            };

            _db.RuleSuggestions.Add(suggestion);
            newSuggestions++;
        }

        if (newSuggestions > 0)
        {
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("ProactiveLearningJob generó {Count} nuevas sugerencias de reglas.", newSuggestions);
        }
    }
}
