using ContableAI.Domain.Enums;

namespace ContableAI.Domain.Common;

/// <summary>
/// Detección de solapamiento entre reglas de clasificación: dos reglas "compiten" cuando sus
/// keywords se contienen mutuamente y sus direcciones son compatibles, de modo que un mismo
/// movimiento podría caer bajo cualquiera de las dos y decide la precedencia
/// (Empresa &gt; Estudio &gt; Sistema, ver <c>HardRuleStrategy</c>).
///
/// Es la contraparte de servidor del criterio que la grilla de reglas ya usaba en el frontend
/// para avisar qué regla propia pisa a cuál general. Vive en Domain para que el aviso de la UI y
/// la validación de la promoción a nivel estudio no puedan divergir.
///
/// No confundir con <see cref="KeywordMatcher"/>: ahí se compara un keyword contra la
/// descripción de un movimiento; acá se comparan dos keywords entre sí.
/// </summary>
public static class RuleConflict
{
    /// <summary>
    /// <c>true</c> si un keyword contiene al otro (en cualquier orden), ignorando mayúsculas,
    /// espacios de borde y espacios repetidos. La contención mutua es deliberadamente laxa: es
    /// un aviso para el usuario, no un bloqueo, y conviene que peque de exceso antes que dejar
    /// pasar un solapamiento real.
    /// </summary>
    public static bool KeywordsOverlap(string? a, string? b)
    {
        var left  = Normalize(a);
        var right = Normalize(b);

        if (left.Length == 0 || right.Length == 0)
            return false;

        return left.Contains(right, StringComparison.Ordinal)
            || right.Contains(left, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>true</c> si las direcciones pueden alcanzar el mismo movimiento. <c>null</c> significa
    /// "débito y crédito", así que es compatible con cualquiera.
    /// </summary>
    public static bool DirectionsCompatible(TransactionType? a, TransactionType? b) =>
        a is null || b is null || a == b;

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                .ToLowerInvariant();
}
