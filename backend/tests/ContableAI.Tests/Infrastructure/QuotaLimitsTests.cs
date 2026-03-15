using ContableAI.Domain.Enums;
using ContableAI.Infrastructure.Services;
using FluentAssertions;

namespace ContableAI.Tests.Infrastructure;

/// <summary>
/// Tests de QuotaLimits.ForPlan().
/// Si alguien cambia los límites del plan, estos tests van a fallar —  eso es intencional.
/// En un sistema contable los límites de facturación son críticos y deben cambiar con conciencia.
/// </summary>
public class QuotaLimitsTests
{
    [Theory]
    [InlineData(StudioPlan.Free,       3,   20,    200)]
    [InlineData(StudioPlan.Pro,       20,  200,   2000)]
    [InlineData(StudioPlan.Enterprise, -1,   -1,     -1)]
    public void ForPlan_ReturnsExpectedLimits(StudioPlan plan, int maxCompanies, int maxRules, int maxTx)
    {
        var limits = QuotaLimits.ForPlan(plan);

        limits.MaxCompanies.Should().Be(maxCompanies,
            because: $"el plan {plan} debe permitir {maxCompanies} empresas");
        limits.MaxRulesPerCompany.Should().Be(maxRules,
            because: $"el plan {plan} debe permitir {maxRules} reglas");
        limits.MaxMonthlyTransactions.Should().Be(maxTx,
            because: $"el plan {plan} debe permitir {maxTx} transacciones/mes");
    }

    [Fact]
    public void ForPlan_Enterprise_AllLimitsAreUnlimited()
    {
        var limits = QuotaLimits.ForPlan(StudioPlan.Enterprise);

        limits.MaxCompanies.Should().Be(-1, "Enterprise no tiene límite de empresas");
        limits.MaxRulesPerCompany.Should().Be(-1, "Enterprise no tiene límite de reglas");
        limits.MaxMonthlyTransactions.Should().Be(-1, "Enterprise no tiene límite de transacciones");
    }

    [Fact]
    public void ForPlan_Free_IsMoreRestrictiveThanPro()
    {
        var free = QuotaLimits.ForPlan(StudioPlan.Free);
        var pro  = QuotaLimits.ForPlan(StudioPlan.Pro);

        free.MaxCompanies.Should().BeLessThan(pro.MaxCompanies);
        free.MaxRulesPerCompany.Should().BeLessThan(pro.MaxRulesPerCompany);
        free.MaxMonthlyTransactions.Should().BeLessThan(pro.MaxMonthlyTransactions);
    }
}
