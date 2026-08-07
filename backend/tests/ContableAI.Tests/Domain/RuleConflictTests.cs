using ContableAI.Domain.Common;
using ContableAI.Domain.Enums;
using FluentAssertions;

namespace ContableAI.Tests.Domain;

/// <summary>
/// Epic F — Criterio de solapamiento entre reglas, usado por el preview de la promoción a nivel
/// estudio para avisar qué empresas van a seguir usando su propia regla.
///
/// Es la contraparte de servidor del aviso que la grilla ya mostraba en el frontend: ambos deben
/// marcar exactamente los mismos pares, o el preview prometería un alcance que la clasificación
/// después no cumple.
/// </summary>
public class RuleConflictTests
{
    [Theory]
    [InlineData("MISMA TITULARIDAD", "MISMA TITULARIDAD")]       // idénticos
    [InlineData("TRANSFERENCIA", "TRANSFER")]                     // uno contiene al otro
    [InlineData("TRANSFER", "TRANSFERENCIA")]                     // y al revés (contención mutua)
    [InlineData("misma titularidad", "MISMA TITULARIDAD")]        // sin distinguir mayúsculas
    [InlineData("  MISMA   TITULARIDAD  ", "MISMA TITULARIDAD")]  // espacios de borde y repetidos
    public void KeywordsOverlap_DetectsCompetingKeywords(string a, string b)
    {
        RuleConflict.KeywordsOverlap(a, b).Should().BeTrue();
    }

    [Theory]
    [InlineData("PROVEEDORES", "SUELDOS")]
    [InlineData("IMP.CHEQUES", "COMISION")]
    public void KeywordsOverlap_IgnoresUnrelatedKeywords(string a, string b)
    {
        RuleConflict.KeywordsOverlap(a, b).Should().BeFalse();
    }

    [Theory]
    [InlineData(null, "ALGO")]
    [InlineData("ALGO", null)]
    [InlineData("", "ALGO")]
    [InlineData("   ", "ALGO")]
    public void KeywordsOverlap_TreatsEmptyAsNoOverlap(string? a, string? b)
    {
        // Un keyword vacío contendría a cualquier otro: sin esta guarda, una regla mal cargada
        // marcaría conflicto contra todas las demás del estudio.
        RuleConflict.KeywordsOverlap(a, b).Should().BeFalse();
    }

    [Fact]
    public void DirectionsCompatible_NullMeansBothDirections()
    {
        RuleConflict.DirectionsCompatible(null, TransactionType.Debit).Should().BeTrue();
        RuleConflict.DirectionsCompatible(TransactionType.Credit, null).Should().BeTrue();
        RuleConflict.DirectionsCompatible(null, null).Should().BeTrue();
    }

    [Fact]
    public void DirectionsCompatible_OppositeDirectionsNeverCompete()
    {
        // Dos reglas con el mismo keyword pero direcciones opuestas nunca alcanzan el mismo
        // movimiento: no son conflicto (ej. "FONDO COMUN" débito vs crédito, que ya conviven
        // en las reglas globales apuntando a cuentas distintas).
        RuleConflict.DirectionsCompatible(TransactionType.Debit, TransactionType.Credit).Should().BeFalse();
        RuleConflict.DirectionsCompatible(TransactionType.Debit, TransactionType.Debit).Should().BeTrue();
    }
}
