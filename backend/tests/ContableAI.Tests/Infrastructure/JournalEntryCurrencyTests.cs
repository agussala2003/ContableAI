using ContableAI.Application.Features.JournalEntries.Commands;
using ContableAI.Domain.Constants;
using FluentAssertions;

namespace ContableAI.Tests.Infrastructure;

/// <summary>
/// Fase C — Generación de asientos multi-moneda: la contrapartida bancaria se elige según la
/// moneda del movimiento, y un movimiento USD sin cuenta en dólares configurada no puede asentarse.
/// </summary>
public class JournalEntryCurrencyTests
{
    private const string ArsAccount = "Banco Galicia - CC Pesos";
    private const string UsdAccount = "Banco Galicia - CC USD";

    // (b) Un asiento en USD busca su propia cuenta bancaria en dólares.
    [Fact]
    public void ResolveBankAccount_UsdMovement_UsesUsdAccount()
    {
        var ok = GenerateJournalEntriesCommandHandler.TryResolveBankAccount(
            Currencies.Usd, ArsAccount, UsdAccount, out var account);

        ok.Should().BeTrue();
        account.Should().Be(UsdAccount);
    }

    [Fact]
    public void ResolveBankAccount_ArsMovement_UsesArsAccount()
    {
        var ok = GenerateJournalEntriesCommandHandler.TryResolveBankAccount(
            Currencies.Ars, ArsAccount, UsdAccount, out var account);

        ok.Should().BeTrue();
        account.Should().Be(ArsAccount);
    }

    // (c) Un asiento en USD falla si la cuenta bancaria USD no está configurada.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveBankAccount_UsdWithoutUsdAccount_Fails(string? usdAccount)
    {
        var ok = GenerateJournalEntriesCommandHandler.TryResolveBankAccount(
            Currencies.Usd, ArsAccount, usdAccount, out var account);

        ok.Should().BeFalse("sin cuenta USD el movimiento en dólares no puede asentarse");
        account.Should().BeEmpty();
    }

    [Fact]
    public void ResolveBankAccount_ArsMovement_IgnoresMissingUsdAccount()
    {
        // Una cuenta que opera en pesos no se ve afectada por no tener cuenta USD.
        var ok = GenerateJournalEntriesCommandHandler.TryResolveBankAccount(
            Currencies.Ars, ArsAccount, null, out var account);

        ok.Should().BeTrue();
        account.Should().Be(ArsAccount);
    }
}
