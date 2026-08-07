using ContableAI.Application.Features.JournalEntries.Commands;
using FluentAssertions;

namespace ContableAI.Tests.Infrastructure;

/// <summary>
/// F1.c — Resolución de la contrapartida de un asiento.
///
/// Antes la elegía la MONEDA del movimiento entre los dos strings de la empresa (uno en pesos y
/// otro en dólares). Ahora es un dato de la CUENTA BANCARIA: una empresa puede tener varias
/// cuentas en la misma moneda y cada una asienta contra la suya.
///
/// Los tres casos que devuelven <c>false</c> son los que impiden asentar y hacen que el generador
/// omita el movimiento sin frenar el lote.
/// </summary>
public class JournalEntryCurrencyTests
{
    private static readonly Guid ArsAccountId = Guid.NewGuid();
    private static readonly Guid UsdAccountId = Guid.NewGuid();
    private static readonly Guid SecondArsAccountId = Guid.NewGuid();

    private const string ArsContra       = "Banco Galicia - CC Pesos";
    private const string UsdContra       = "Banco Galicia - CC USD";
    private const string SecondArsContra = "Banco BBVA - CC Pesos";

    private static readonly Dictionary<Guid, string> ContraAccounts = new()
    {
        [ArsAccountId]       = ArsContra,
        [UsdAccountId]       = UsdContra,
        [SecondArsAccountId] = SecondArsContra,
    };

    [Fact]
    public void EachBankAccount_ResolvesToItsOwnContraAccount()
    {
        GenerateJournalEntriesCommandHandler
            .TryResolveContraAccount(ArsAccountId, ContraAccounts, out var ars).Should().BeTrue();
        ars.Should().Be(ArsContra);

        GenerateJournalEntriesCommandHandler
            .TryResolveContraAccount(UsdAccountId, ContraAccounts, out var usd).Should().BeTrue();
        usd.Should().Be(UsdContra);
    }

    [Fact]
    public void TwoAccountsInTheSameCurrency_ResolveToDifferentContraAccounts()
    {
        // El caso que el modelo viejo no podía representar: dos cuentas en pesos de la misma
        // empresa compartían obligatoriamente la única contrapartida de Company.BankAccountName.
        GenerateJournalEntriesCommandHandler
            .TryResolveContraAccount(ArsAccountId, ContraAccounts, out var first).Should().BeTrue();
        GenerateJournalEntriesCommandHandler
            .TryResolveContraAccount(SecondArsAccountId, ContraAccounts, out var second).Should().BeTrue();

        first.Should().NotBe(second);
    }

    [Fact]
    public void TransactionWithoutBankAccount_CannotBeBooked()
    {
        // Movimiento legacy anterior al alta de cuentas bancarias.
        var ok = GenerateJournalEntriesCommandHandler
            .TryResolveContraAccount(null, ContraAccounts, out var account);

        ok.Should().BeFalse();
        account.Should().BeEmpty();
    }

    [Fact]
    public void UnknownBankAccount_CannotBeBooked()
    {
        var ok = GenerateJournalEntriesCommandHandler
            .TryResolveContraAccount(Guid.NewGuid(), ContraAccounts, out var account);

        ok.Should().BeFalse("una cuenta que no está en el mapa no puede resolver contrapartida");
        account.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ProvisionalAccount_WithoutContraAccount_CannotBeBooked(string contraAccountName)
    {
        // Cuenta provisional: la creó el enrutamiento del OCR o el backfill, recibe movimientos,
        // pero todavía no tiene contrapartida cargada.
        var provisionalId = Guid.NewGuid();
        var accounts = new Dictionary<Guid, string> { [provisionalId] = contraAccountName };

        var ok = GenerateJournalEntriesCommandHandler
            .TryResolveContraAccount(provisionalId, accounts, out var account);

        ok.Should().BeFalse("sin contrapartida configurada el movimiento no puede asentarse");
        account.Should().BeEmpty();
    }

    [Fact]
    public void ContraAccountName_IsTrimmed()
    {
        var id = Guid.NewGuid();
        var accounts = new Dictionary<Guid, string> { [id] = "  Banco Nación CC  " };

        GenerateJournalEntriesCommandHandler
            .TryResolveContraAccount(id, accounts, out var account).Should().BeTrue();
        account.Should().Be("Banco Nación CC");
    }
}
