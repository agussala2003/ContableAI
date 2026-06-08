using ContableAI.Domain.Common;
using FluentAssertions;

namespace ContableAI.Tests.Domain;

/// <summary>
/// Tests de KeywordNormalizer (UX-04): descripciones que solo difieren en los números
/// finales deben colapsar al mismo keyword para agrupar patrones repetidos.
/// </summary>
public class KeywordNormalizerTests
{
    [Theory]
    [InlineData("FACTURA0012", "FACTURA")]
    [InlineData("FACTURA0034", "FACTURA")]
    [InlineData("RAPIPAGO12345", "RAPIPAGO")]
    public void Normalize_StripsTrailingDigits(string input, string expected)
    {
        KeywordNormalizer.Normalize(input).Should().Be(expected);
    }

    [Fact]
    public void Normalize_TrailingNumberVariants_CollapseToSameKeyword()
    {
        // El corazón de UX-04: dos comprobantes que solo cambian el número final agrupan.
        KeywordNormalizer.Normalize("FACTURA0012")
            .Should().Be(KeywordNormalizer.Normalize("FACTURA0034"));
    }

    [Fact]
    public void Normalize_DropsPureNumberAndBracketTokens()
    {
        KeywordNormalizer.Normalize("TRANSFER 12345").Should().Be("TRANSFER");
        KeywordNormalizer.Normalize("PAGO PROV [ref 99]").Should().Be("PAGO PROV");
    }

    [Fact]
    public void Normalize_KeepsAlphaStemOfReferenceCodes_DropsPureNumber()
    {
        // CCP339 → CCP (prefijo constante, agrupa igual); 313226 → se descarta.
        KeywordNormalizer.Normalize("TRANSF. CLIENTE CTA. CCP339 313226")
            .Should().Be("TRANSF. CLIENTE CTA. CCP");
    }

    [Fact]
    public void Normalize_MidWordDigits_FallBackToFullDescription()
    {
        // "BBVA2024NET" tiene dígitos internos (no finales) → token descartado → fallback al original.
        KeywordNormalizer.Normalize("BBVA2024NET").Should().Be("BBVA2024NET");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_NullOrEmpty_ReturnsEmpty(string? input)
    {
        KeywordNormalizer.Normalize(input).Should().BeEmpty();
    }

    [Fact]
    public void Normalize_AllDigits_FallsBackToOriginalUppercased()
    {
        KeywordNormalizer.Normalize("12345").Should().Be("12345");
    }
}
