using ContableAI.Domain.Constants;
using ContableAI.Domain.Entities;
using ContableAI.Infrastructure.Services;
using FluentAssertions;

namespace ContableAI.Tests.Infrastructure;

/// <summary>
/// Fase B del soporte multi-moneda: el parser detecta la moneda a nivel documento y la estampa
/// en todas las transacciones del extracto. Los extractos de Galicia en dólares deben quedar
/// 100% USD (manteniendo el fix de la fila Total), y el resto del corpus 100% ARS.
/// </summary>
public class CurrencyDetectionTests
{
    private static readonly string ExtractsDir =
        @"C:\Users\aguss\Documents\Projects\ContableAI\tests\extractos";

    private static IReadOnlyList<BankTransaction> Parse(string relativePath)
    {
        var path = Path.Combine(ExtractsDir, relativePath);
        if (!File.Exists(path)) return [];
        var parser = new PdfBankParser();
        using var stream = File.OpenRead(path);
        return parser.Parse(stream, Path.GetFileName(path)).ToList();
    }

    // ── Galicia USD → USD ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(@"GALICIA USD\Extracto_Cuentas_Galicia_2024_11_29.pdf", 10)]
    [InlineData(@"GALICIA USD\Extracto_Cuentas_Galicia_2024_12_30.pdf", 3)]
    [InlineData(@"GALICIA USD\Extracto_Cuentas_Galicia_2025_01_31 (1).pdf", 5)]
    public void Parse_GaliciaUsd_AllTransactionsAreUsd(string relativePath, int expectedCount)
    {
        var txs = Parse(relativePath);
        if (txs.Count == 0) return; // PDF no disponible en este entorno, omitir

        txs.Should().HaveCount(expectedCount, "la fila de totales no se cuenta (fix Fase 2 intacto)");
        txs.Should().OnlyContain(t => t.Currency == Currencies.Usd,
            $"{relativePath}: todo el extracto es en dólares");

        // El fix de la fila Total debe seguir vigente bajo detección USD
        txs.Should().NotContain(t => t.Description.Contains("-USD"));
        txs.Should().NotContain(
            t => t.Description.StartsWith("Total", StringComparison.OrdinalIgnoreCase) ||
                 t.Description.EndsWith("Total", StringComparison.OrdinalIgnoreCase));
    }

    // ── Resto del corpus → ARS ────────────────────────────────────────────────

    [Theory]
    [InlineData(@"BBVA FALLAS 15-7-2026\1225 (1).pdf")]        // BBVA pesos ("CC $")
    [InlineData(@"BBVA FALLAS 15-7-2026\0925.pdf")]
    [InlineData(@"GALICIA\Extracto_Cuentas_Galicia_2025_04_30 (2).pdf")] // Galicia pesos
    [InlineData(@"GALICIA\Extracto_Cuentas_Galicia_2024_01_31 2.pdf")]
    [InlineData(@"CREDICOOP\Abril_2025.pdf")]
    [InlineData(@"BANCO CIUDAD\Ciudad 092024 - 082025.pdf")]   // formato US, pero cuenta en pesos
    [InlineData(@"MERCADO PAGO\Mayo_2025.pdf")]
    public void Parse_RestOfCorpus_AllTransactionsAreArs(string relativePath)
    {
        var txs = Parse(relativePath);
        if (txs.Count == 0) return;

        txs.Should().OnlyContain(t => t.Currency == Currencies.Ars,
            $"{relativePath}: cuenta en pesos, ninguna transacción debe quedar en USD");
    }

    // ── Tabla de decisión (incluye rechazo de extracto mixto) ─────────────────

    [Fact]
    public void ResolveCurrency_UsdHeader_ReturnsUsd()
    {
        PdfBankParser.ResolveCurrency(usdHeader: true, arsHeader: false, usdAmountTokens: 0)
            .Should().Be(Currencies.Usd);
    }

    [Fact]
    public void ResolveCurrency_ArsHeader_ReturnsArs()
    {
        PdfBankParser.ResolveCurrency(usdHeader: false, arsHeader: true, usdAmountTokens: 0)
            .Should().Be(Currencies.Ars);
    }

    [Fact]
    public void ResolveCurrency_ArsHeader_WinsOverStrayUsdTokens()
    {
        // Una cuenta en pesos que menciona USD en descripciones no debe clasificarse como USD.
        PdfBankParser.ResolveCurrency(usdHeader: false, arsHeader: true, usdAmountTokens: 5)
            .Should().Be(Currencies.Ars);
    }

    [Theory]
    [InlineData(0, Currencies.Ars)]
    [InlineData(1, Currencies.Ars)] // por debajo del umbral
    [InlineData(2, Currencies.Usd)] // umbral alcanzado
    [InlineData(9, Currencies.Usd)]
    public void ResolveCurrency_NoHeader_UsesTokenThreshold(int tokens, string expected)
    {
        PdfBankParser.ResolveCurrency(usdHeader: false, arsHeader: false, usdAmountTokens: tokens)
            .Should().Be(expected);
    }

    [Fact]
    public void ResolveCurrency_BothHeaders_ThrowsMixedCurrencyError()
    {
        var act = () => PdfBankParser.ResolveCurrency(usdHeader: true, arsHeader: true, usdAmountTokens: 0);

        act.Should().Throw<InvalidOperationException>()
           .WithMessage(PdfBankParser.MixedCurrencyError);
    }
}
