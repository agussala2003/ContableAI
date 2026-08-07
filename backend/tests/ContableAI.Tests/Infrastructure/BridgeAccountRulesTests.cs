using ContableAI.Domain.Entities;
using ContableAI.Domain.Enums;
using ContableAI.Infrastructure.Services;
using ContableAI.Infrastructure.Services.Classification;
using FluentAssertions;

namespace ContableAI.Tests.Infrastructure;

/// <summary>
/// v1.1 / Epic C — Las transferencias entre cuentas propias se clasifican contra la cuenta puente
/// <see cref="GlobalRules.BridgeAccount"/>, no contra una cuenta de resultado.
///
/// El riesgo que cubren estos tests es de precedencia: varias de estas descripciones empiezan con
/// "TRANSFER", así que también matchean las reglas de cobros de clientes (CUENTAS A COBRAR, 11) y
/// pagos a proveedores (PROVEEDORES, 14-15). Sin <see cref="GlobalRules.BridgePriority"/> por
/// encima de esas, una transferencia propia se contabilizaría como venta o como gasto real.
/// </summary>
public class BridgeAccountRulesTests
{
    private static async Task<string?> ClassifyAsync(string description, TransactionType type)
    {
        var tx = new BankTransaction
        {
            Date        = new DateOnly(2026, 3, 10),
            Description = description,
            Amount      = 100_000m,
            Type        = type,
        };

        await new HardRuleStrategy().TryClassifyAsync(tx, [.. GlobalRules.GetDefaults()], splitChequeTax: false);
        return tx.AssignedAccount;
    }

    [Theory]
    [InlineData("TRANSFERENCIA MISMA TITULARIDAD")]
    [InlineData("TRANSFERENCIA ENTRE CUENTAS PROPIAS")]
    [InlineData("TRANSF A CTA PROPIA")]
    [InlineData("TRANSFERENCIA MISMO TITULAR")]
    [InlineData("TRASPASO ENTRE CUENTAS")]
    public async Task OwnAccountTransfer_ClassifiesToBridgeAccount_InBothDirections(string description)
    {
        // Débito: el otro candidato es PROVEEDORES (regla "TRANSFER", prioridad 15).
        (await ClassifyAsync(description, TransactionType.Debit))
            .Should().Be(GlobalRules.BridgeAccount);

        // Crédito: el otro candidato es TARJETAS DE CREDITO (regla "TRANSFER", prioridad 12).
        (await ClassifyAsync(description, TransactionType.Credit))
            .Should().Be(GlobalRules.BridgeAccount);
    }

    [Fact]
    public async Task ThirdPartyTransfer_StillClassifiesToItsOwnAccount()
    {
        // Guarda contra el sobre-alcance: subir la prioridad del puente no debe capturar
        // transferencias de terceros, que siguen siendo cobros y pagos reales.
        (await ClassifyAsync("TRANSFERENCIA DE TERCEROS", TransactionType.Credit))
            .Should().Be("CUENTAS A COBRAR");

        (await ClassifyAsync("TRANSF. A TERCEROS", TransactionType.Debit))
            .Should().Be("PROVEEDORES");
    }

    [Fact]
    public void BridgeAccount_IsSeededInDefaultChartOfAccounts()
    {
        GlobalRules.GetDefaultAccounts().Should().Contain(GlobalRules.BridgeAccount);
    }

    [Fact]
    public void BridgeKeywords_CoverEveryRuleTargetingTheBridge()
    {
        // BridgeKeywords alimenta el repunte del seeder: si una regla nueva apunta al puente y
        // queda fuera de esta lista, las bases existentes nunca la corrigen.
        GlobalRules.BridgeKeywords.Should().BeEquivalentTo(
            GlobalRules.GetDefaults()
                .Where(r => r.TargetAccount == GlobalRules.BridgeAccount)
                .Select(r => r.Keyword));

        GlobalRules.BridgeKeywords.Should().Contain("MISMA TITULARIDAD");
    }
}
