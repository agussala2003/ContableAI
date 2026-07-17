using ContableAI.Domain.Constants;
using ContableAI.Domain.Entities;
using ContableAI.Domain.Enums;
using FluentAssertions;

namespace ContableAI.Tests.Domain;

/// <summary>
/// Fase A del soporte multi-moneda: garantiza que toda entidad recién creada asume pesos (ARS)
/// por defecto. Es el invariante que respalda la migración aditiva — los datos ya existentes de
/// los clientes se backfillean como ARS, así que ningún código nuevo debe cambiar ese default
/// sin una decisión explícita.
/// </summary>
public class CurrencyDefaultsTests
{
    [Fact]
    public void BankTransaction_DefaultsToArs()
    {
        var tx = new BankTransaction
        {
            Date        = new DateOnly(2025, 1, 15),
            Description = "TEST",
            Amount      = 1000m,
            Type        = TransactionType.Debit,
        };

        tx.Currency.Should().Be(Currencies.Ars);
    }

    [Fact]
    public void JournalEntry_DefaultsToArs()
    {
        var entry = new JournalEntry { Date = new DateOnly(2025, 1, 15) };

        entry.Currency.Should().Be(Currencies.Ars);
    }

    [Fact]
    public void AfipVoucher_DefaultsToArs()
    {
        var voucher = new AfipVoucher { Date = new DateOnly(2025, 1, 15), Amount = 500m };

        voucher.Currency.Should().Be(Currencies.Ars);
    }

    [Fact]
    public void Company_UsdBankAccount_DefaultsToNull()
    {
        var company = new Company { Name = "ACME SRL", Cuit = "30-11111111-9" };

        company.UsdBankAccountName.Should().BeNull(
            "una empresa que no opera en dólares no tiene cuenta USD configurada");
    }

    [Theory]
    [InlineData("ARS", true)]
    [InlineData("USD", true)]
    [InlineData("EUR", false)]
    [InlineData("ars", false)] // los códigos ISO son mayúsculas; el parser normaliza antes de validar
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Currencies_IsSupported_MatchesKnownCodes(string? code, bool expected)
    {
        Currencies.IsSupported(code).Should().Be(expected);
    }
}
