using ContableAI.Domain.Common;
using ContableAI.Domain.Constants;
using ContableAI.Domain.Entities;
using ContableAI.Domain.Enums;
using ContableAI.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ContableAI.Infrastructure.BackgroundJobs;

public class ProactiveLearningService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<ProactiveLearningService> _logger;

    public ProactiveLearningService(IServiceProvider services, ILogger<ProactiveLearningService> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ProactiveLearningService is starting.");

        // Pequeño delay inicial para que la app termine de arrancar
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await AnalyzeTransactionsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error ocurrido ejecutando ProactiveLearningService.");
            }

            await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
        }
    }

    private async Task AnalyzeTransactionsAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ContableAIDbContext>();

        var cutoffDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-730));

        // Fetch into memory so we can apply normalized keyword comparison
        var raw = await db.BankTransactions
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

            bool ruleExists = await db.AccountingRules
                .AnyAsync(r => r.CompanyId == group.CompanyId && r.Keyword == group.Keyword, ct);

            if (ruleExists) continue;

            var existingSuggestion = await db.RuleSuggestions
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

            db.RuleSuggestions.Add(suggestion);
            newSuggestions++;
        }

        if (newSuggestions > 0)
        {
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("ProactiveLearningService generó {Count} nuevas sugerencias de reglas.", newSuggestions);
        }
    }
}
