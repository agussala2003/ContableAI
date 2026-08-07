using ContableAI.Domain.Common;
using FluentAssertions;

namespace ContableAI.Tests.Domain;

/// <summary>
/// Epic D — Criterio único de coincidencia keyword ↔ descripción.
///
/// Antes había tres criterios distintos conviviendo: el motor de clasificación hacía matching
/// palabra por palabra ignorando mayúsculas, aceptar-sugerencia armaba un ILIKE equivalente, y
/// reaplicar una regla usaba <c>Description.Contains(keyword)</c> — que en Postgres distingue
/// mayúsculas y exige la frase literal completa. Resultado: reaplicar alcanzaba a menos
/// movimientos de los que la regla realmente clasifica.
/// </summary>
public class KeywordMatcherTests
{
    [Theory]
    // Palabras en orden, con texto intercalado: el caso que rompe un Contains literal.
    [InlineData("COELSA 12345 EMPRESA SA", "COELSA EMPRESA SA")]
    [InlineData("TRANSF. A TERCEROS 998877", "TRANSF. A TERCEROS")]
    // Sin distinguir mayúsculas en ninguna dirección.
    [InlineData("transferencia misma titularidad", "MISMA TITULARIDAD")]
    [InlineData("TRANSFERENCIA MISMA TITULARIDAD", "misma titularidad")]
    // Palabra única contenida dentro de otra.
    [InlineData("TRANSFERENCIA RECIBIDA", "TRANSFER")]
    public void Matches_ReturnsTrue_ForWordsInOrder(string description, string keyword)
    {
        KeywordMatcher.Matches(description, keyword).Should().BeTrue();
    }

    [Theory]
    // Las palabras están, pero en el orden inverso al del keyword.
    [InlineData("EMPRESA SA COELSA", "COELSA EMPRESA")]
    // Falta una palabra.
    [InlineData("COELSA SA", "COELSA EMPRESA SA")]
    // Un keyword vacío no debe capturar todo.
    [InlineData("CUALQUIER COSA", "")]
    [InlineData("CUALQUIER COSA", "   ")]
    public void Matches_ReturnsFalse_WhenSequenceDoesNotHold(string description, string keyword)
    {
        KeywordMatcher.Matches(description, keyword).Should().BeFalse();
    }

    [Fact]
    public void Matches_HandlesNullsWithoutThrowing()
    {
        KeywordMatcher.Matches(null, "ALGO").Should().BeFalse();
        KeywordMatcher.Matches("ALGO", null).Should().BeFalse();
    }

    [Fact]
    public void ToLikePattern_BuildsOneSegmentPerWord()
    {
        KeywordMatcher.ToLikePattern("COELSA EMPRESA SA")
            .Should().Be("%COELSA%EMPRESA%SA%");

        // Espacios de más no generan segmentos vacíos.
        KeywordMatcher.ToLikePattern("  MISMA   TITULARIDAD ")
            .Should().Be("%MISMA%TITULARIDAD%");
    }

    [Fact]
    public void ToLikePattern_EscapesLikeWildcards()
    {
        // Sin escapar, un keyword con % o _ se convertiría en comodín y capturaría de más.
        KeywordMatcher.ToLikePattern("100% NETO")
            .Should().Be("%100\\%%NETO%");

        KeywordMatcher.ToLikePattern("A_B")
            .Should().Be("%A\\_B%");

        KeywordMatcher.ToLikePattern("C:\\PAGOS")
            .Should().Be("%C:\\\\PAGOS%");
    }

    [Fact]
    public void Matches_IsStricterThanTheLikePattern_ForOutOfOrderWords()
    {
        // Documenta por qué el prefiltro SQL se reconfirma en memoria: el patrón y Matches
        // coinciden en el orden, y quien filtre por ILIKE debe cerrar con Matches para que la
        // decisión final la tome exactamente el mismo código que corre en la clasificación.
        const string description = "EMPRESA SA COELSA";

        KeywordMatcher.ToLikePattern("COELSA EMPRESA").Should().Be("%COELSA%EMPRESA%");
        KeywordMatcher.Matches(description, "COELSA EMPRESA").Should().BeFalse();
    }
}
