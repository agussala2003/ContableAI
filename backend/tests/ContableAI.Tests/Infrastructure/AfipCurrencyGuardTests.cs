using ContableAI.Domain.Constants;
using ContableAI.Domain.Entities;
using ContableAI.Domain.Enums;
using ContableAI.Infrastructure.Features.Afip;
using ContableAI.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace ContableAI.Tests.Infrastructure;

/// <summary>
/// Fase C — Guard multi-moneda en la conciliación AFIP: los VEPs de ARCA son siempre en pesos,
/// así que un movimiento bancario en USD nunca debe cruzarse con ellos (ni 1:1 ni por combos),
/// aunque su importe coincida numéricamente con la sumatoria de VEPs.
/// </summary>
public class AfipCurrencyGuardTests
{
    private static ContableAIDbContext NewInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<ContableAIDbContext>()
            .UseInMemoryDatabase($"afip-currency-{Guid.NewGuid()}")
            .Options;
        return new ContableAIDbContext(options);
    }

    private static BankTransaction TxDebit(Guid companyId, decimal amount, string currency) => new()
    {
        Date        = new DateOnly(2025, 2, 24),
        Description = "TRANSFERENCIA A FIP",
        Amount      = amount,
        Type        = TransactionType.Debit,
        Currency    = currency,
        CompanyId   = companyId,
    };

    private static AfipVoucher Voucher(Guid companyId, decimal amount, string tax) => new()
    {
        CompanyId = companyId,
        Date      = new DateOnly(2025, 2, 24),
        Amount    = amount,
        TaxName   = tax,
        Currency  = Currencies.Ars,
    };

    private static async Task SeedTaxMatchingTxAsync(ContableAIDbContext db, BankTransaction tx)
    {
        // NeedsTaxMatching se activa a través de Assign(..., needsTaxMatching: true).
        tx.Assign("AFIP a Determinar", null, needsTaxMatching: true, ClassificationSources.HardRule, 1.0f);
        db.BankTransactions.Add(tx);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Combinations_UsdMovement_ProducesNoSuggestions()
    {
        using var db = NewInMemoryDb();
        var companyId = Guid.NewGuid();

        // Dos VEPs en pesos que suman 436.251,10
        db.AfipVouchers.Add(Voucher(companyId, 422_350.68m, "IVA A Pagar"));
        db.AfipVouchers.Add(Voucher(companyId, 13_900.42m, "Pago IIBB"));
        await db.SaveChangesAsync();

        // Movimiento en USD con ESE MISMO importe: no debe generar combos
        await SeedTaxMatchingTxAsync(db, TxDebit(companyId, 436_251.10m, Currencies.Usd));

        var service = new AfipCombinationService(db);
        var suggestions = await service.ComputeSuggestionsAsync(companyId);

        suggestions.Should().BeEmpty("un movimiento en USD nunca dispara la búsqueda de combos");
    }

    [Fact]
    public async Task Combinations_ArsMovement_ProducesSuggestion_Control()
    {
        using var db = NewInMemoryDb();
        var companyId = Guid.NewGuid();

        db.AfipVouchers.Add(Voucher(companyId, 422_350.68m, "IVA A Pagar"));
        db.AfipVouchers.Add(Voucher(companyId, 13_900.42m, "Pago IIBB"));
        await db.SaveChangesAsync();

        // Control: el mismo importe en ARS SÍ debe sugerir la combinación
        await SeedTaxMatchingTxAsync(db, TxDebit(companyId, 436_251.10m, Currencies.Ars));

        var service = new AfipCombinationService(db);
        var suggestions = await service.ComputeSuggestionsAsync(companyId);

        suggestions.Should().ContainSingle();
        suggestions[0].Alternatives.Should().ContainSingle();
        suggestions[0].Alternatives[0].Vouchers.Should().HaveCount(2);
    }
}
