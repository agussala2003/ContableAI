using ContableAI.Domain.Constants;
using ContableAI.Domain.Entities;
using ContableAI.Domain.Enums;
using ContableAI.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContableAI.Tests.Infrastructure;

/// <summary>
/// Tests de la lógica de interpretación de <see cref="PdfBankParser"/> alimentada con fixtures de
/// TEXTO (no PDFs). Gracias a la abstracción <see cref="IStatementTextExtractor"/>, se inyecta un
/// <see cref="FixtureTextExtractor"/> que traduce texto monoespaciado column-aligned al modelo
/// posicional, ejercitando de verdad la detección de columnas, la matemática débito/crédito y la
/// exclusión de filas resumen — con datos ficticios y aserciones reales, sin datos sensibles.
/// </summary>
public class PdfBankParserFixtureTests
{
    private static List<BankTransaction> ParseFixture(string fixtureFile, string bank)
    {
        var extractor = FixtureTextExtractor.FromFixture(fixtureFile, bank);
        var parser    = new PdfBankParser(extractor, NullLogger<PdfBankParser>.Instance);
        return parser.Parse(Stream.Null, fixtureFile).ToList();
    }

    // ── BBVA: clasificación por columna + matemática de partida ────────────────

    [Fact]
    public void Bbva_ClassifiesAmountsByColumn_AndKeepsDatesDescriptionsAndTotals()
    {
        var txs = ParseFixture("bbva_mock_statement.txt", "BBVA");

        txs.Should().HaveCount(4);
        txs.Should().OnlyContain(t => t.Currency == Currencies.Ars);
        txs.Should().OnlyContain(t => t.SourceBank == "BBVA");
        txs.Should().BeInAscendingOrder(t => t.Date);

        // Cada movimiento: la columna (DEBITO/CREDITO) define el tipo; el importe es exacto.
        txs.Should().ContainSingle(t =>
            t.Date == new DateOnly(2025, 6, 1) && t.Type == TransactionType.Debit &&
            t.Amount == 1500.50m && t.Description == "PAGO PROVEEDOR FICTICIO SA");

        txs.Should().ContainSingle(t =>
            t.Date == new DateOnly(2025, 6, 2) && t.Type == TransactionType.Credit &&
            t.Amount == 2000.00m && t.Description == "COBRO CLIENTE FICTICIO SA");

        txs.Should().ContainSingle(t =>
            t.Date == new DateOnly(2025, 6, 3) && t.Type == TransactionType.Debit &&
            t.Amount == 350.00m && t.Description == "COMISION MANTENIMIENTO");

        txs.Should().ContainSingle(t =>
            t.Date == new DateOnly(2025, 6, 4) && t.Type == TransactionType.Credit &&
            t.Amount == 10000.00m && t.Description == "TRANSFERENCIA RECIBIDA");

        // Sumas por tipo (matemática de la interpretación de columnas).
        txs.Where(t => t.Type == TransactionType.Debit).Sum(t => t.Amount).Should().Be(1850.50m);
        txs.Where(t => t.Type == TransactionType.Credit).Sum(t => t.Amount).Should().Be(12000.00m);

        // La columna SALDO nunca debe colarse como movimiento.
        txs.Should().NotContain(t => t.Amount == 123456.78m || t.Amount == 135106.78m);
    }

    // ── Galicia: excluye la fila resumen "Total retención de impuestos" ────────

    [Fact]
    public void Galicia_ExcludesRetentionSummaryRow_AndParsesRealMovements()
    {
        var txs = ParseFixture("galicia_mock_statement.txt", "GALICIA");

        txs.Should().HaveCount(3, "la fila 'Total retencion de impuestos' es un resumen, no un movimiento");
        txs.Should().OnlyContain(t => t.SourceBank == "GALICIA");
        txs.Should().OnlyContain(t => t.Currency == Currencies.Ars);

        // El importe de la fila resumen (9.999,99) no debe existir como transacción.
        txs.Should().NotContain(t => t.Amount == 9999.99m);
        txs.Should().NotContain(t => t.Description.Contains("Total retencion", StringComparison.OrdinalIgnoreCase));

        txs.Where(t => t.Type == TransactionType.Debit).Sum(t => t.Amount).Should().Be(46234.56m);
        txs.Where(t => t.Type == TransactionType.Credit).Sum(t => t.Amount).Should().Be(80000.00m);

        txs.Should().ContainSingle(t =>
            t.Type == TransactionType.Credit && t.Amount == 80000.00m &&
            t.Description == "ACREDITACION COBRANZA");
    }

    // ── El parser ya no toca disco/PDF: la interpretación corre solo con texto ─

    [Fact]
    public void Parser_RunsPurelyFromInjectedText_NoPdfNeeded()
    {
        // Un extractor en memoria (sin archivo) confirma que PdfBankParser depende solo de la
        // abstracción: el mismo pipeline de interpretación produce el movimiento esperado.
        const string text =
            "Cuenta Corriente en Pesos\n" +
            "FECHA       DETALLE                               DEBITO     CREDITO         SALDO\n" +
            "05/08/2025  PAGO SERVICIOS FICTICIOS            2.500,00                 50.000,00\n";

        var extractor = FixtureTextExtractor.FromText(text, "GENERIC");
        var parser    = new PdfBankParser(extractor, NullLogger<PdfBankParser>.Instance);

        var txs = parser.Parse(Stream.Null, "in-memory").ToList();

        txs.Should().ContainSingle();
        txs[0].Type.Should().Be(TransactionType.Debit);
        txs[0].Amount.Should().Be(2500.00m);
        txs[0].Description.Should().Be("PAGO SERVICIOS FICTICIOS");
    }
}
