using ContableAI.Domain.Entities;
using ContableAI.Domain.Enums;
using ContableAI.Infrastructure.Services;
using FluentAssertions;

namespace ContableAI.Tests.Infrastructure;

/// <summary>
/// Regresión del bug reportado el 15-7-2026 con extractos Galicia en dólares: la fila de
/// totales del final ("Total | USD 639,40 | -USD 31,78 | USD 607,62") se colaba como un
/// movimiento más. En pesos esa fila se filtraba porque sus celdas de moneda ("$", "-$")
/// no aportan letras, pero en dólares los tokens "USD"/"-USD" sí, y el filtro
/// IsGaliciaSplitSummaryAmountRow no la reconocía como resumen. Además, en algunos
/// extractos la palabra "Total" queda en una fila Y aparte y se anexaba a la descripción
/// del último movimiento real.
/// </summary>
public class GaliciaUsdParseTests
{
    private static readonly string BaseDir = TestData.PathTo("extractos", "GALICIA USD");

    /// <summary>Parsea el PDF pedido; si no está disponible, salta el test (Skipped, no Passed).</summary>
    private static IReadOnlyList<BankTransaction> Parse(string fileName)
    {
        var path = Path.Combine(BaseDir, fileName);
        TestData.RequireFile(path);
        var parser = new PdfBankParser();
        using var stream = File.OpenRead(path);
        return parser.Parse(stream, fileName).ToList();
    }

    // Totales de débitos del período de cada extracto: el bug los insertaba como un débito más.
    [SkippableTheory]
    [InlineData("Extracto_Cuentas_Galicia_2024_11_29.pdf", 10, "31.78")]
    [InlineData("Extracto_Cuentas_Galicia_2024_12_30.pdf", 3, "12.16")]
    [InlineData("Extracto_Cuentas_Galicia_2025_01_31 (1).pdf", 5, "651.56")]
    public void Parse_ExcludesTotalsRow(string fileName, int expectedCount, string totalDebitsRaw)
    {
        var txs = Parse(fileName);

        Console.WriteLine($"=== {fileName} — {txs.Count} transacciones ===");
        foreach (var tx in txs)
            Console.WriteLine($"  {tx.Date} | {tx.Type,-6} | {tx.Amount,10:F2} | {tx.Description}");

        txs.Should().HaveCount(expectedCount,
            $"{fileName}: la fila de totales no debe contarse como movimiento");

        // La fila fantasma tenía como descripción los tokens de moneda y/o la palabra Total
        txs.Should().NotContain(t => t.Description.Contains("-USD"),
            "los tokens de moneda de la fila de totales no deben aparecer como descripción");
        txs.Should().NotContain(
            t => t.Description.StartsWith("Total", StringComparison.OrdinalIgnoreCase) ||
                 t.Description.EndsWith("Total", StringComparison.OrdinalIgnoreCase),
            "la etiqueta 'Total' no debe formar parte de ninguna descripción");

        // El importe del total de débitos no existe como movimiento real en estos extractos
        var totalDebits = decimal.Parse(totalDebitsRaw, System.Globalization.CultureInfo.InvariantCulture);
        txs.Should().NotContain(t => t.Amount == totalDebits,
            $"el total de débitos ({totalDebits}) no debe insertarse como transacción");
    }

    [SkippableFact]
    public void Parse_November_KeepsRealMovementsIntact()
    {
        var txs = Parse("Extracto_Cuentas_Galicia_2024_11_29.pdf");

        // El único crédito real del período debe seguir presente y con su descripción
        var credit = txs.SingleOrDefault(t => t.Type == TransactionType.Credit);
        credit.Should().NotBeNull();
        credit!.Amount.Should().Be(639.40m);
        credit.Description.Should().Contain("COMEXT.ORDEN DE PAGO");
    }
}
