using ContableAI.Domain.Common;
using ContableAI.Domain.Constants;
using ContableAI.Domain.Entities;
using ContableAI.Infrastructure.BackgroundJobs;
using ContableAI.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContableAI.Tests.Infrastructure;

/// <summary>
/// Tests del análisis proactivo acotado por empresa (P-7): misma semántica que la versión
/// plataforma-completa (umbral ≥ 3, idempotencia, respeto de reglas existentes y de
/// sugerencias rechazadas) pero procesando empresa por empresa.
/// </summary>
public class ProactiveLearningJobTests
{
    private static ContableAIDbContext CtxFor(string dbName) =>
        new(new DbContextOptionsBuilder<ContableAIDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options);

    private static ProactiveLearningJob JobFor(ContableAIDbContext ctx) =>
        new(ctx, NullLogger<ProactiveLearningJob>.Instance);

    private static void SeedManualTxs(ContableAIDbContext ctx, Guid companyId, string tenantId,
        string description, string account, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var tx = new BankTransaction
            {
                Description    = description,
                Date           = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-i)),
                CompanyId      = companyId,
                TenantId       = tenantId,
                StudioTenantId = tenantId,
            };
            tx.Assign(account, null, false, ClassificationSources.Manual);
            ctx.BankTransactions.Add(tx);
        }
    }

    [Fact]
    public async Task Analyze_CreatesSuggestionPerCompany_AndIsIdempotent()
    {
        var db = nameof(Analyze_CreatesSuggestionPerCompany_AndIsIdempotent);
        var companyA = Guid.NewGuid();
        var companyB = Guid.NewGuid();

        using (var seed = CtxFor(db))
        {
            SeedManualTxs(seed, companyA, "studio-1", "PAGO EDESUR FACTURA", "Luz", count: 3);
            SeedManualTxs(seed, companyB, "studio-2", "PAGO EDESUR FACTURA", "Servicios", count: 3);
            SeedManualTxs(seed, companyB, "studio-2", "TRANSFERENCIA VARIA", "Otros", count: 2); // bajo umbral
            seed.SaveChanges();
        }

        using (var ctx = CtxFor(db))
            await JobFor(ctx).AnalyzeTransactionsAsync();

        using (var check = CtxFor(db))
        {
            var suggestions = await check.RuleSuggestions.ToListAsync();
            suggestions.Should().HaveCount(2, "una por empresa que superó el umbral; el grupo de 2 no llega");
            suggestions.Should().Contain(s => s.CompanyId == companyA && s.SuggestedAccount == "Luz" && s.Frequency == 3);
            suggestions.Should().Contain(s => s.CompanyId == companyB && s.SuggestedAccount == "Servicios" && s.Frequency == 3);
        }

        // Segunda corrida: no duplica.
        using (var ctx = CtxFor(db))
            await JobFor(ctx).AnalyzeTransactionsAsync();

        using (var check = CtxFor(db))
            (await check.RuleSuggestions.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Analyze_SkipsWhenRuleExists_AndDoesNotReactivateRejected()
    {
        var db = nameof(Analyze_SkipsWhenRuleExists_AndDoesNotReactivateRejected);
        var withRule  = Guid.NewGuid();
        var rejected  = Guid.NewGuid();

        string keyword;
        using (var seed = CtxFor(db))
        {
            SeedManualTxs(seed, withRule, "studio-r", "PAGO METROGAS SA", "Gas", count: 4);
            SeedManualTxs(seed, rejected, "studio-x", "PAGO METROGAS SA", "Gas", count: 4);
            keyword = KeywordNormalizer.Normalize("PAGO METROGAS SA");

            seed.AccountingRules.Add(new AccountingRule { CompanyId = withRule, Keyword = keyword, TargetAccount = "Gas" });
            seed.RuleSuggestions.Add(new RuleSuggestion
            {
                CompanyId = rejected, TenantId = "studio-x", Keyword = keyword,
                SuggestedAccount = "Gas", Frequency = 4, Status = SuggestionStatus.Rejected,
            });
            seed.SaveChanges();
        }

        using (var ctx = CtxFor(db))
            await JobFor(ctx).AnalyzeTransactionsAsync();

        using (var check = CtxFor(db))
        {
            // Empresa con regla: sin sugerencia nueva. Empresa con Rejected: se respeta el rechazo.
            (await check.RuleSuggestions.CountAsync(s => s.CompanyId == withRule)).Should().Be(0);
            var stillRejected = await check.RuleSuggestions.SingleAsync(s => s.CompanyId == rejected);
            stillRejected.Status.Should().Be(SuggestionStatus.Rejected,
                "el job no reactiva rechazadas; solo una nueva asignación manual lo hace");
        }
    }
}
