using ContableAI.Infrastructure.Services;
using FluentAssertions;

namespace ContableAI.Tests.Infrastructure;

/// <summary>
/// Tests de canonicalización de nombres de cuenta (FIX-A): el mismo concepto cargado con
/// distinta casing o espacios debe resolver a una única cuenta canónica del plan.
/// </summary>
public class AccountNameResolverTests
{
    private static CanonicalAccountMap Map(params string[] accounts) => new(accounts);

    [Fact]
    public void Resolve_DifferentCasing_ReturnsCanonical()
    {
        var map = Map("Cargas Sociales", "IVA A Pagar");

        map.Resolve("cargas sociales").Should().Be("Cargas Sociales");
        map.Resolve("CARGAS SOCIALES").Should().Be("Cargas Sociales");
        map.Resolve("Cargas Sociales").Should().Be("Cargas Sociales");
    }

    [Fact]
    public void Resolve_TrimsSurroundingWhitespace()
    {
        var map = Map("Cargas Sociales");

        map.Resolve("  cargas sociales  ").Should().Be("Cargas Sociales");
    }

    [Fact]
    public void Resolve_UnknownAccount_ReturnsTrimmedInput()
    {
        var map = Map("Cargas Sociales");

        // Cuenta libre que aún no está en el plan: se conserva tal cual (trimmeada).
        map.Resolve("  Gastos Varios  ").Should().Be("Gastos Varios");
    }

    [Fact]
    public void Resolve_NullOrEmpty_ReturnsEmpty()
    {
        var map = Map("Cargas Sociales");

        map.Resolve(null).Should().BeEmpty();
        map.Resolve("").Should().BeEmpty();
        map.Resolve("   ").Should().BeEmpty();
    }

    [Fact]
    public void Constructor_GlobalLoadedFirst_WinsOverStudioVariant()
    {
        // Las globales se cargan primero; una variante posterior con distinta casing no la pisa.
        var map = Map("Cargas Sociales", "cargas sociales");

        map.Resolve("CARGAS SOCIALES").Should().Be("Cargas Sociales");
    }

    [Fact]
    public void Constructor_IgnoresBlankAccountNames()
    {
        var map = Map("Cargas Sociales", "", "   ");

        map.Resolve("cargas sociales").Should().Be("Cargas Sociales");
        map.Resolve("").Should().BeEmpty();
    }
}
