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
    [InlineData(StudioPlan.Free,        0,    0,      0)]   // Free bloqueado: requiere upgrade para operar
    [InlineData(StudioPlan.Pro,        15,  250,     -1)]   // Pro: 15 empresas, 250 reglas, tx ilimitadas (-1)
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

        // -1 representa "ilimitado" (el máximo), por eso se normaliza a int.MaxValue para comparar.
        static int Effective(int limit) => limit == -1 ? int.MaxValue : limit;

        Effective(free.MaxCompanies).Should().BeLessThan(Effective(pro.MaxCompanies));
        Effective(free.MaxRulesPerCompany).Should().BeLessThan(Effective(pro.MaxRulesPerCompany));
        Effective(free.MaxMonthlyTransactions).Should().BeLessThan(Effective(pro.MaxMonthlyTransactions));
    }
}
