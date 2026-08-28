using ContableAI.Domain.Constants;
using FluentAssertions;

namespace ContableAI.Tests.Domain;

/// <summary>
/// El catálogo de bancos es la fuente de verdad de la Fase C: lo consultan la validación del alta
/// de cuentas, el filtro por banco de las tres grillas y el selector del formulario. Estos tests
/// existen por un bug concreto: la lista estaba duplicada y hardcodeada en el frontend, se le había
/// quedado Santander afuera, y el síntoma no era un error sino una cuenta que no aparecía en su
/// propio filtro. Un catálogo con una sola definición y verificada evita repetirlo.
/// </summary>
public class BankCodesTests
{
    [Theory]
    [InlineData(BankCodes.Bbva)]
    [InlineData(BankCodes.Galicia)]
    [InlineData(BankCodes.Santander)]
    [InlineData(BankCodes.Credicoop)]
    [InlineData(BankCodes.MercadoPago)]
    [InlineData(BankCodes.Ciudad)]
    public void All_ContainsEverySupportedBank(string code)
    {
        BankCodes.All.Should().Contain(code);
        BankCodes.IsSupported(code).Should().BeTrue();
    }

    [Fact]
    public void All_DoesNotOfferGenericAsABank()
    {
        // GENERIC no es un banco: es lo que devuelve la detección cuando no reconoce el extracto.
        // Si se colara en el catálogo, aparecería como opción en el alta de cuentas y como bucket
        // del filtro, y el usuario podría asignarle a una cuenta un "banco" que no existe.
        BankCodes.All.Should().NotContain(BankCodes.Generic);
        BankCodes.IsSupported(BankCodes.Generic).Should().BeFalse();
    }

    [Fact]
    public void EverySupportedBank_HasItsOwnDisplayName()
    {
        var labels = BankCodes.All.Select(BankCodes.DisplayName).ToList();

        labels.Should().OnlyHaveUniqueItems("dos bancos con la misma etiqueta son indistinguibles en el dropdown");
        labels.Should().OnlyContain(l => !string.IsNullOrWhiteSpace(l));
    }

    [Theory]
    [InlineData("santander",   BankCodes.Santander)]
    [InlineData("  GALICIA  ", BankCodes.Galicia)]
    [InlineData("BbVa",        BankCodes.Bbva)]
    public void Normalize_AcceptsAnyCasingAndTrimsWhitespace(string input, string expected)
    {
        // El valor puede llegar de una query string escrita a mano; lo que se persistió siempre
        // está en mayúsculas.
        BankCodes.Normalize(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("BANCO_INEXISTENTE")]
    [InlineData("GENERIC")]
    public void Normalize_RejectsWhatIsNotInTheCatalog(string? input)
    {
        // Devolver null es lo que hace que el endpoint responda 400 en vez de ignorar el filtro en
        // silencio: un filtro ignorado devuelve de más, y el contador no se entera.
        BankCodes.Normalize(input).Should().BeNull();
        BankCodes.IsSupported(input).Should().BeFalse();
    }

    [Fact]
    public void DisplayName_FallsBackToTheRawCode()
    {
        // Una cuenta cuyo banco quedó fuera del catálogo tiene que seguir siendo visible: mostrar
        // el código crudo es peor que una etiqueta linda, pero mucho mejor que una fila en blanco.
        BankCodes.DisplayName("BANCO_VIEJO").Should().Be("BANCO_VIEJO");
        BankCodes.DisplayName(null).Should().BeEmpty();
    }

    // ── Banco emisor a partir del CBU ──────────────────────────────────────────

    [Theory]
    [InlineData("0070999030004000123456", BankCodes.Galicia)]
    [InlineData("0170099040000012345678", BankCodes.Bbva)]
    [InlineData("0290000100000012345678", BankCodes.Ciudad)]
    [InlineData("0720575020000000016140", BankCodes.Santander)]
    [InlineData("1910019055001234567890", BankCodes.Credicoop)]
    public void FromCbu_IdentifiesTheIssuerByItsBcraCode(string cbu, string expected)
    {
        BankCodes.FromCbu(cbu).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("072057502000000001614")]    // 21 dígitos: no es un CBU
    [InlineData("07205750200000000161400")]  // 23 dígitos
    [InlineData("072-0575-020000000016140")] // sin normalizar a dígitos
    [InlineData("0000003100059687283158")]   // CVU de billetera virtual: no identifica la entidad
    [InlineData("0110599520000001234567")]   // banco fuera del catálogo (Nación)
    public void FromCbu_ReturnsNullWhenItCannotIdentifyTheIssuer(string? cbu)
    {
        // Null es un resultado válido: la detección cae al método siguiente (el nombre en el
        // texto) en vez de adivinar un banco. Adivinarlo elige la estrategia de parseo equivocada.
        BankCodes.FromCbu(cbu).Should().BeNull();
    }
}
