using ContableAI.Application.Features.JournalEntries.Commands;
using ContableAI.Domain.Constants;
using ContableAI.Domain.Entities;
using ContableAI.Domain.Enums;
using ContableAI.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContableAI.Tests.Infrastructure;

/// <summary>
/// Tests del generador de asientos (<see cref="GenerateJournalEntriesCommandHandler"/>), el
/// output contable del producto. Cubren lo esencial: que cada asiento cuadre en partida doble
/// (Debe == Haber), el desglose de un pago agrupado de AFIP en una línea por impuesto, y la
/// deduplicación por firma que evita asentar dos veces el mismo movimiento.
///
/// Usan EF Core InMemory con el filtro multi-tenant desactivado (contexto sin tenant): el
/// handler ya filtra por <c>StudioTenantId</c> explícitamente, así que el test no necesita
/// simular un usuario autenticado.
/// </summary>
public class GenerateJournalEntriesHandlerTests
{
    private const string Studio = "studio-test";
    private const string ArsBankAccount = "Banco Nación Cta Cte";
    private const string UsdBankAccount = "Banco Nación Cta Cte USD";

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ContableAIDbContext NewDb(string dbName) =>
        new(new DbContextOptionsBuilder<ContableAIDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options); // tenant = null → filtro multi-tenant OFF

    private static GenerateJournalEntriesCommandHandler HandlerFor(ContableAIDbContext db) =>
        new(db, NullLogger<GenerateJournalEntriesCommandHandler>.Instance);

    private static Company NewCompany(bool withUsdAccount = false) => new()
    {
        Name           = "Empresa Test SRL",
        StudioTenantId = Studio,
        IsActive       = true,
        BankAccountName    = ArsBankAccount,
        UsdBankAccountName = withUsdAccount ? UsdBankAccount : null,
    };

    /// <summary>Crea una transacción ya clasificada (con cuenta asignada) lista para asentar.</summary>
    private static BankTransaction NewTx(
        Guid companyId,
        decimal amount,
        TransactionType type,
        string account,
        string description = "Movimiento test",
        string currency = Currencies.Ars,
        string source = ClassificationSources.HardRule,
        DateOnly? date = null)
    {
        var tx = new BankTransaction
        {
            Date        = date ?? new DateOnly(2025, 6, 15),
            Description = description,
            Amount      = amount,
            Type        = type,
            Currency    = currency,
            CompanyId   = companyId,
            TenantId    = companyId.ToString(),
        };
        tx.Assign(account, null, needsTaxMatching: false, source);
        return tx;
    }

    /// <summary>Ejecuta el handler contra una copia fresca del contexto (mismo store InMemory).</summary>
    private static async Task Generate(string dbName, params Guid[] txIds)
    {
        await using var db = NewDb(dbName);
        await HandlerFor(db).Handle(new GenerateJournalEntriesCommand([.. txIds], Studio), CancellationToken.None);
    }

    private static void AssertBalanced(JournalEntry entry)
    {
        var debe  = entry.Lines.Where(l => l.IsDebit).Sum(l => l.Amount);
        var haber = entry.Lines.Where(l => !l.IsDebit).Sum(l => l.Amount);
        debe.Should().Be(haber, "todo asiento de partida doble debe cuadrar (Debe == Haber)");
    }

    // ── 1. Débito simple: cuenta asignada al Debe, banco al Haber ──────────────

    [Fact]
    public async Task Debit_GeneratesBalancedEntry_AssignedAccountOnDebe()
    {
        var dbName = Guid.NewGuid().ToString();
        var company = NewCompany();
        var tx = NewTx(company.Id, 1500.50m, TransactionType.Debit, "Proveedores");

        await using (var seed = NewDb(dbName))
        {
            seed.Companies.Add(company);
            seed.BankTransactions.Add(tx);
            await seed.SaveChangesAsync();
        }

        await Generate(dbName, tx.Id);

        await using var db = NewDb(dbName);
        var entry = await db.JournalEntries.Include(e => e.Lines).SingleAsync();

        AssertBalanced(entry);
        entry.Lines.Should().HaveCount(2);
        entry.Currency.Should().Be(Currencies.Ars);
        entry.Lines.Single(l => l.IsDebit).Account.Should().Be("Proveedores");
        entry.Lines.Single(l => !l.IsDebit).Account.Should().Be(ArsBankAccount);
        entry.Lines.Should().OnlyContain(l => l.Amount == 1500.50m);

        var persistedTx = await db.BankTransactions.SingleAsync();
        persistedTx.JournalEntryId.Should().Be(entry.Id, "la transacción debe quedar vinculada a su asiento");
    }

    // ── 2. Crédito simple: banco al Debe, cuenta asignada al Haber ─────────────

    [Fact]
    public async Task Credit_GeneratesBalancedEntry_BankOnDebe()
    {
        var dbName = Guid.NewGuid().ToString();
        var company = NewCompany();
        var tx = NewTx(company.Id, 2000m, TransactionType.Credit, "Ventas");

        await using (var seed = NewDb(dbName))
        {
            seed.Companies.Add(company);
            seed.BankTransactions.Add(tx);
            await seed.SaveChangesAsync();
        }

        await Generate(dbName, tx.Id);

        await using var db = NewDb(dbName);
        var entry = await db.JournalEntries.Include(e => e.Lines).SingleAsync();

        AssertBalanced(entry);
        entry.Lines.Single(l => l.IsDebit).Account.Should().Be(ArsBankAccount);
        entry.Lines.Single(l => !l.IsDebit).Account.Should().Be("Ventas");
    }

    // ── 3. Impuesto al cheque: 50% Débitos + 50% Créditos contra el banco ──────

    [Fact]
    public async Task ChequeTaxSplit_SplitsIntoTwoTaxDebits_AgainstBank()
    {
        var dbName = Guid.NewGuid().ToString();
        var company = NewCompany();
        // Importe impar para verificar el redondeo del split (half1 + half2 == total).
        var tx = NewTx(company.Id, 100.01m, TransactionType.Debit, "Impuesto al Cheque",
            source: ClassificationSources.ChequeTaxSplit);

        await using (var seed = NewDb(dbName))
        {
            seed.Companies.Add(company);
            seed.BankTransactions.Add(tx);
            await seed.SaveChangesAsync();
        }

        await Generate(dbName, tx.Id);

        await using var db = NewDb(dbName);
        var entry = await db.JournalEntries.Include(e => e.Lines).SingleAsync();

        AssertBalanced(entry);
        entry.Lines.Should().HaveCount(3);

        var debitLines = entry.Lines.Where(l => l.IsDebit).ToList();
        debitLines.Should().HaveCount(2);
        debitLines.Select(l => l.Account).Should().BeEquivalentTo(new[]
        {
            "Impuesto a los Débitos Bancarios",
            "Impuesto a los Créditos Bancarios",
        });
        debitLines.Sum(l => l.Amount).Should().Be(100.01m, "las dos mitades deben sumar exactamente el total");

        var bankLine = entry.Lines.Single(l => !l.IsDebit);
        bankLine.Account.Should().Be(ArsBankAccount);
        bankLine.Amount.Should().Be(100.01m);
    }

    // ── 4. Cruce múltiple AFIP: una línea de débito por impuesto ───────────────

    [Fact]
    public async Task AfipComboMatch_GeneratesOneDebitLinePerTax_AgainstBank()
    {
        var dbName = Guid.NewGuid().ToString();
        var company = NewCompany();
        var tx = NewTx(company.Id, 300m, TransactionType.Debit, "Pago AFIP agrupado",
            source: ClassificationSources.AfipComboMatch);

        // Dos VEPs de distinto impuesto cuya suma == importe del movimiento.
        var vIva  = new AfipVoucher { CompanyId = company.Id, Amount = 200m, TaxName = "IVA A Pagar",
                                       Date = tx.Date, MatchedTransactionId = tx.Id, IsMatched = true };
        var vIibb = new AfipVoucher { CompanyId = company.Id, Amount = 100m, TaxName = "Pago IIBB",
                                      Date = tx.Date, MatchedTransactionId = tx.Id, IsMatched = true };

        await using (var seed = NewDb(dbName))
        {
            seed.Companies.Add(company);
            seed.BankTransactions.Add(tx);
            seed.AfipVouchers.AddRange(vIva, vIibb);
            await seed.SaveChangesAsync();
        }

        await Generate(dbName, tx.Id);

        await using var db = NewDb(dbName);
        var entry = await db.JournalEntries.Include(e => e.Lines).SingleAsync();

        AssertBalanced(entry);
        entry.Lines.Should().HaveCount(3, "una línea por impuesto (2) + el banco (1)");

        var debits = entry.Lines.Where(l => l.IsDebit).ToList();
        debits.Should().Contain(l => l.Account == "IVA A Pagar" && l.Amount == 200m);
        debits.Should().Contain(l => l.Account == "Pago IIBB"   && l.Amount == 100m);

        var bank = entry.Lines.Single(l => !l.IsDebit);
        bank.Account.Should().Be(ArsBankAccount);
        bank.Amount.Should().Be(300m);
    }

    // ── 4b. VEPs del mismo impuesto se consolidan en una sola línea ────────────

    [Fact]
    public async Task AfipComboMatch_SameTaxVouchers_AreConsolidatedIntoOneLine()
    {
        var dbName = Guid.NewGuid().ToString();
        var company = NewCompany();
        var tx = NewTx(company.Id, 500m, TransactionType.Debit, "Pago AFIP mismo impuesto",
            source: ClassificationSources.AfipComboMatch);

        var v1 = new AfipVoucher { CompanyId = company.Id, Amount = 300m, TaxName = "IVA A Pagar",
                                   Date = tx.Date, MatchedTransactionId = tx.Id, IsMatched = true };
        var v2 = new AfipVoucher { CompanyId = company.Id, Amount = 200m, TaxName = "IVA A Pagar",
                                   Date = tx.Date, MatchedTransactionId = tx.Id, IsMatched = true };

        await using (var seed = NewDb(dbName))
        {
            seed.Companies.Add(company);
            seed.BankTransactions.Add(tx);
            seed.AfipVouchers.AddRange(v1, v2);
            await seed.SaveChangesAsync();
        }

        await Generate(dbName, tx.Id);

        await using var db = NewDb(dbName);
        var entry = await db.JournalEntries.Include(e => e.Lines).SingleAsync();

        AssertBalanced(entry);
        entry.Lines.Should().HaveCount(2, "dos VEPs del mismo impuesto → una línea de débito consolidada + banco");
        entry.Lines.Single(l => l.IsDebit).Should().Match<JournalEntryLine>(
            l => l.Account == "IVA A Pagar" && l.Amount == 500m);
    }

    // ── 4c. Si los VEPs no suman el total, cae al asiento genérico (2 líneas) ──

    [Fact]
    public async Task AfipComboMatch_WhenVouchersDoNotSumToAmount_FallsBackToGenericEntry()
    {
        var dbName = Guid.NewGuid().ToString();
        var company = NewCompany();
        var tx = NewTx(company.Id, 300m, TransactionType.Debit, "Pago AFIP inconsistente",
            source: ClassificationSources.AfipComboMatch);

        // La suma (150 + 100 = 250) NO coincide con el importe (300): el guard debe rechazar el desglose.
        var v1 = new AfipVoucher { CompanyId = company.Id, Amount = 150m, TaxName = "IVA A Pagar",
                                   Date = tx.Date, MatchedTransactionId = tx.Id, IsMatched = true };
        var v2 = new AfipVoucher { CompanyId = company.Id, Amount = 100m, TaxName = "Pago IIBB",
                                   Date = tx.Date, MatchedTransactionId = tx.Id, IsMatched = true };

        await using (var seed = NewDb(dbName))
        {
            seed.Companies.Add(company);
            seed.BankTransactions.Add(tx);
            seed.AfipVouchers.AddRange(v1, v2);
            await seed.SaveChangesAsync();
        }

        await Generate(dbName, tx.Id);

        await using var db = NewDb(dbName);
        var entry = await db.JournalEntries.Include(e => e.Lines).SingleAsync();

        AssertBalanced(entry);
        entry.Lines.Should().HaveCount(2, "sin desglose por impuesto: asiento genérico cuenta-asignada / banco");
        // Fallback genérico de un débito: la cuenta asignada va al Debe y el banco al Haber.
        entry.Lines.Single(l => l.IsDebit).Account.Should().Be("Pago AFIP inconsistente");
        entry.Lines.Single(l => l.IsDebit).Amount.Should().Be(300m);
        entry.Lines.Single(l => !l.IsDebit).Account.Should().Be(ArsBankAccount);
    }

    // ── 5. Deduplicación por firma: no se asienta dos veces el mismo movimiento ─

    [Fact]
    public async Task Duplicate_SameSignature_DoesNotCreateSecondEntry_AndMarksTransaction()
    {
        var dbName = Guid.NewGuid().ToString();
        var company = NewCompany();
        var date = new DateOnly(2025, 6, 10);

        var txA = NewTx(company.Id, 999.99m, TransactionType.Debit, "Servicios", "Pago Edenor", date: date);
        var txB = NewTx(company.Id, 999.99m, TransactionType.Debit, "Servicios", "Pago Edenor", date: date);

        await using (var seed = NewDb(dbName))
        {
            seed.Companies.Add(company);
            seed.BankTransactions.AddRange(txA, txB);
            await seed.SaveChangesAsync();
        }

        // Primer asiento: crea la entrada.
        await Generate(dbName, txA.Id);
        // Segundo movimiento idéntico: debe deduplicarse contra el asiento existente.
        await Generate(dbName, txB.Id);

        await using var db = NewDb(dbName);
        var entries = await db.JournalEntries.ToListAsync();
        entries.Should().HaveCount(1, "el segundo movimiento idéntico no debe generar un asiento nuevo");

        var persistedB = await db.BankTransactions.SingleAsync(t => t.Id == txB.Id);
        persistedB.IsPossibleDuplicate.Should().BeTrue();
        persistedB.JournalEntryId.Should().Be(entries[0].Id, "el duplicado apunta al asiento existente");
    }

    // ── 6. USD sin cuenta en dólares: se omite el movimiento sin frenar el lote ─

    [Fact]
    public async Task UsdTransaction_WithoutUsdAccount_IsSkipped_WithoutAbortingArsEntries()
    {
        var dbName = Guid.NewGuid().ToString();
        var company = NewCompany(withUsdAccount: false); // solo cuenta en pesos
        var arsTx = NewTx(company.Id, 1000m, TransactionType.Debit, "Proveedores", "Pago pesos");
        var usdTx = NewTx(company.Id, 50m, TransactionType.Debit, "Honorarios", "Pago dólares",
            currency: Currencies.Usd);

        await using (var seed = NewDb(dbName))
        {
            seed.Companies.Add(company);
            seed.BankTransactions.AddRange(arsTx, usdTx);
            await seed.SaveChangesAsync();
        }

        await Generate(dbName, arsTx.Id, usdTx.Id);

        await using var db = NewDb(dbName);
        var entries = await db.JournalEntries.Include(e => e.Lines).ToListAsync();
        entries.Should().HaveCount(1, "el movimiento ARS se asienta; el USD sin cuenta en dólares se omite");
        entries[0].Currency.Should().Be(Currencies.Ars);

        var persistedUsd = await db.BankTransactions.SingleAsync(t => t.Id == usdTx.Id);
        persistedUsd.JournalEntryId.Should().BeNull("el movimiento en USD queda sin asentar");
    }

    // ── 7. Período cerrado: se cancela toda la generación del lote ─────────────

    [Fact]
    public async Task ClosedPeriod_CancelsGeneration_NoEntriesCreated()
    {
        var dbName = Guid.NewGuid().ToString();
        var company = NewCompany();
        var date = new DateOnly(2025, 3, 20);
        var tx = NewTx(company.Id, 500m, TransactionType.Debit, "Proveedores", date: date);

        await using (var seed = NewDb(dbName))
        {
            seed.Companies.Add(company);
            seed.BankTransactions.Add(tx);
            seed.ClosedPeriods.Add(new ClosedPeriod
            {
                StudioTenantId = Studio, Year = date.Year, Month = date.Month, ClosedByEmail = "owner@test.com",
            });
            await seed.SaveChangesAsync();
        }

        await Generate(dbName, tx.Id);

        await using var db = NewDb(dbName);
        (await db.JournalEntries.CountAsync()).Should().Be(0, "un período cerrado bloquea la generación");
        (await db.BankTransactions.SingleAsync()).JournalEntryId.Should().BeNull();
    }
}
