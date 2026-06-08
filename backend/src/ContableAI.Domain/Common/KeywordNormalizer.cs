using System.Text.RegularExpressions;

namespace ContableAI.Domain.Common;

/// <summary>
/// Normaliza la descripción de un movimiento a un "keyword" estable para detectar patrones
/// repetidos (sugerencias proactivas de reglas). Fuente única — antes había 3 copias.
/// </summary>
public static partial class KeywordNormalizer
{
    /// <summary>
    /// Quita el contenido entre corchetes/paréntesis, descarta los <b>números al final</b> de cada
    /// token (UX-04: "FACTURA0012" y "FACTURA0034" → "FACTURA") y los tokens que quedan con dígitos
    /// internos o vacíos. Devuelve el resultado en mayúsculas; si queda vacío, usa la descripción completa.
    /// </summary>
    public static string Normalize(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return string.Empty;

        var withoutBrackets = BracketContent().Replace(description.Trim(), " ");

        var words = withoutBrackets
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => TrailingDigits().Replace(w, ""))   // UX-04: quitar números al final del token
            .Where(w => w.Length > 0 && !w.Any(char.IsDigit)) // descartar vacíos o con dígitos internos
            .ToArray();

        var result = string.Join(' ', words).ToUpperInvariant().Trim();
        return string.IsNullOrWhiteSpace(result) ? description.Trim().ToUpperInvariant() : result;
    }

    [GeneratedRegex(@"\[.*?\]|\(.*?\)")]
    private static partial Regex BracketContent();

    [GeneratedRegex(@"\d+$")]
    private static partial Regex TrailingDigits();
}
