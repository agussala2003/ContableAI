using ContableAI.Infrastructure.Services;
using FluentAssertions;

namespace ContableAI.Tests.Infrastructure;

/// <summary>
/// Tests para BankParserHelpers.ParseAmount.
/// Este método es crítico: un error de parsing convierte "1.234,56" en un monto incorrecto.
/// Los extractos bancarios argentinos pueden tener distintos separadores según el banco.
/// </summary>
public class BankAmountParserTests
{
    // ─── BankParserHelpers.ParseAmount ───────────────────────────────────────
    // Implementación: elimina comas y espacios, luego parsea con InvariantCulture
    // → Funciona con formato donde coma=miles y punto=decimal (ej: "1,234.56")

    [Theory]
    [InlineData("1234.56",     1234.56)]
    [InlineData("1,234.56",    1234.56)]   // coma como miles → elimina la coma
    [InlineData("1 234.56",    1234.56)]    // espacio como miles → elimina el espacio
    [InlineData("-500.00",     -500.00)]
    [InlineData("0",           0.0)]
    [InlineData("100",         100.0)]
    public void ParseAmount_ValidInput_ReturnsExpectedDecimal(string raw, double expected)
    {
        var result = BankParserHelpers.ParseAmount(raw);
        result.Should().Be((decimal)expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ParseAmount_EmptyOrNull_ReturnsZero(string? raw)
    {
        var result = BankParserHelpers.ParseAmount(raw!);
        result.Should().Be(0m);
    }

    [Theory]
    [InlineData("DEBE")]
    [InlineData("N/A")]
    [InlineData("---")]
    public void ParseAmount_NonNumericInput_ReturnsZero(string raw)
    {
        var result = BankParserHelpers.ParseAmount(raw);
        result.Should().Be(0m);
    }

    [Fact]
    public void ParseAmount_NegativeWithSign_ReturnsNegative()
    {
        BankParserHelpers.ParseAmount("-1500.75").Should().Be(-1500.75m);
    }

    [Fact]
    public void ParseAmount_IsUsedToCalculateDebitsAndCredits_NeverNaN()
    {
        // Propiedad fundamental: ParseAmount nunca debe retornar NaN o lanzar excepción
        string[] bankSamples = ["1234.00", "0.01", "-9999999.99", "", " ", "###", "1,000,000.00"];
        foreach (var sample in bankSamples)
        {
            var act = () => BankParserHelpers.ParseAmount(sample);
            act.Should().NotThrow($"ParseAmount('{sample}') lanzó excepción");
        }
    }
}
