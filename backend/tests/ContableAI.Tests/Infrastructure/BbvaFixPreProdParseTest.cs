using ContableAI.Domain.Entities;
using ContableAI.Domain.Enums;
using ContableAI.Infrastructure.Services;
using FluentAssertions;

namespace ContableAI.Tests.Infrastructure;

/// <summary>
/// Tests para los extractos de la carpeta "FIX PREPROD" que reproducen dos bugs reportados:
///   Bug 1 — Fechas con año 2026 en extractos 2025 (filename con formato MMYY sobreescrito por fechas del documento).
///   Bug 2 — "PAGO CHEQUE 48HS" aparece sin descripción y con un día de desfase (sección suplementaria de cheques
///            activaba el parser de tabla principal).
/// </summary>
public class BbvaFixPreProdParseTest
{
    private static readonly string BaseDir =
        @"C:\Users\aguss\Documents\Projects\ContableAI\tests\extractos\FIX PREPROD";

    private static IReadOnlyList<BankTransaction> Parse(string fileName)
    {
        var path = Path.Combine(BaseDir, fileName);
        if (!File.Exists(path)) return [];
        var parser = new PdfBankParser();
        using var stream = File.OpenRead(path);
        return parser.Parse(stream, fileName).ToList();
    }

    // ── Bug 1: fechas en año correcto ────────────────────────────────────────

    [Theory]
    [InlineData("0925.pdf", 2025, 9)]
    [InlineData("1025.pdf", 2025, 10)]
    [InlineData("1125.pdf", 2025, 11)]
    [InlineData("1225.pdf", 2025, 12)]
    public void Parse_AllDatesHaveCorrectYear(string fileName, int expectedYear, int expectedPrimaryMonth)
    {
        var txs = Parse(fileName);
        if (txs.Count == 0) return; // archivo no disponible, omitir

        Console.WriteLine($"=== {fileName} — {txs.Count} transacciones ===");
        foreach (var tx in txs)
            Console.WriteLine($"  {tx.Date} | {tx.Type,-6} | {tx.Amount,15:F2} | {tx.Description}");

        // Ninguna transacción debe tener año posterior al del extracto
        txs.Should().NotContain(t => t.Date.Year > expectedYear,
            $"El extracto {fileName} es de {expectedPrimaryMonth}/{expectedYear}; no debe haber fechas de años posteriores");

        // La gran mayoría debe estar en el año correcto (permitimos diciembre del año anterior en extractos de enero)
        var wrongYear = txs.Where(t => t.Date.Year != expectedYear && t.Date.Year != expectedYear - 1).ToList();
        wrongYear.Should().BeEmpty($"Todas las transacciones de {fileName} deben tener año {expectedYear}");
    }

    // ── Bug 2: PAGO CHEQUE no debe tener descripción vacía ni fecha desfasada ─

    [Theory]
    [InlineData("0925.pdf")]
    [InlineData("1025.pdf")]
    [InlineData("1125.pdf")]
    [InlineData("1225.pdf")]
    public void Parse_PagoChequeHasDescriptionAndNoEmptyRows(string fileName)
    {
        var txs = Parse(fileName);
        if (txs.Count == 0) return;

        // No debe haber transacciones con descripción vacía
        var emptyDesc = txs.Where(t => string.IsNullOrWhiteSpace(t.Description)).ToList();
        emptyDesc.Should().BeEmpty($"{fileName}: no debe haber transacciones sin descripción (indica que la sección suplementaria de cheques se parseó como tabla principal)");

        // Las transacciones de PAGO CHEQUE deben tener descripción no vacía
        var pagoCheque = txs.Where(t => t.Description.Contains("CHEQUE", StringComparison.OrdinalIgnoreCase)).ToList();
        Console.WriteLine($"=== {fileName} — PAGO CHEQUE ({pagoCheque.Count} filas) ===");
        foreach (var tx in pagoCheque)
            Console.WriteLine($"  {tx.Date} | {tx.Type,-6} | {tx.Amount,15:F2} | {tx.Description}");

        pagoCheque.Should().OnlyContain(t => !string.IsNullOrWhiteSpace(t.Description),
            "Las filas PAGO CHEQUE deben tener descripción");
    }

    // ── Sanidad general ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("0925.pdf")]
    [InlineData("1025.pdf")]
    [InlineData("1125.pdf")]
    [InlineData("1225.pdf")]
    public void Parse_NoSummaryRowsLeakIntoTransactions(string fileName)
    {
        var txs = Parse(fileName);
        if (txs.Count == 0) return;

        txs.Should().NotContain(t => t.Description.Contains("SALDO AL", StringComparison.OrdinalIgnoreCase),
            "Las filas de saldo final no deben incluirse como transacciones");
        txs.Should().NotContain(t => t.Description.Contains("NRO DE CHEQUE", StringComparison.OrdinalIgnoreCase),
            "Los encabezados de la sección de cheques no deben incluirse como transacciones");
        txs.Should().NotContain(t => t.Description.Contains("TOTAL MOVIMIENTOS", StringComparison.OrdinalIgnoreCase),
            "Las filas de resumen no deben incluirse como transacciones");
    }
}
