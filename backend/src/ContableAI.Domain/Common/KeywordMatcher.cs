namespace ContableAI.Domain.Common;

/// <summary>
/// Criterio ÚNICO de coincidencia entre la descripción de un movimiento y el keyword de una regla.
///
/// La semántica es "subsecuencia de subcadenas": cada palabra del keyword debe aparecer en la
/// descripción, en orden, admitiendo texto intermedio. Así el keyword "COELSA EMPRESA SA" matchea
/// "COELSA 12345 EMPRESA SA", donde los códigos numéricos variables rompen un <c>Contains</c> literal.
///
/// Existe en dos formas que deben mantenerse equivalentes:
///   · <see cref="Matches"/> — evaluación en memoria (motor de clasificación).
///   · <see cref="ToLikePattern"/> — patrón <c>ILIKE</c> para filtrar del lado de Postgres.
///
/// Son equivalentes por construcción: <c>%w1%w2%…%wn%</c> es exactamente "w1, luego w2, luego …".
/// Buscar la primera ocurrencia de cada palabra (lo que hace <see cref="Matches"/>) es óptimo para
/// este problema: si <c>wi+1</c> aparece después de una ocurrencia tardía de <c>wi</c>, también
/// aparece después de la primera. Aun así, quien filtre por SQL debería reconfirmar con
/// <see cref="Matches"/> antes de escribir, porque <c>ILIKE</c> resuelve mayúsculas según la
/// collation de la base y <see cref="Matches"/> usa <see cref="StringComparison.OrdinalIgnoreCase"/>.
///
/// Deliberadamente NO se quitan acentos: el motor de clasificación tampoco lo hace, y aplicar
/// <c>f_unaccent</c> solo de un lado haría que una reaplicación tocara movimientos que el motor
/// nunca habría clasificado con esa regla.
/// </summary>
public static class KeywordMatcher
{
    /// <summary>Carácter de escape a pasar como tercer argumento de <c>EF.Functions.ILike</c>.</summary>
    public const string LikeEscapeChar = "\\";

    /// <summary>
    /// <c>true</c> si cada palabra de <paramref name="keyword"/> aparece en
    /// <paramref name="description"/>, en orden y sin distinguir mayúsculas.
    /// Un keyword vacío no matchea nada (evita que una regla mal cargada capture todo).
    /// </summary>
    public static bool Matches(string? description, string? keyword)
    {
        if (string.IsNullOrWhiteSpace(description) || string.IsNullOrWhiteSpace(keyword))
            return false;

        int pos = 0;
        foreach (var word in SplitWords(keyword))
        {
            int idx = description.IndexOf(word, pos, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return false;
            pos = idx + word.Length;
        }

        return true;
    }

    /// <summary>
    /// Patrón <c>ILIKE</c> equivalente a <see cref="Matches"/>: <c>%palabra1%palabra2%…%</c>.
    /// Escapa los comodines de LIKE (<c>\</c>, <c>%</c>, <c>_</c>) para que un keyword que los
    /// contenga se busque literalmente. Usar junto con <see cref="LikeEscapeChar"/>.
    /// </summary>
    public static string ToLikePattern(string? keyword)
    {
        var words = SplitWords(keyword ?? string.Empty).Select(EscapeLikeWildcards);
        return "%" + string.Join("%", words) + "%";
    }

    private static string[] SplitWords(string keyword) =>
        keyword.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static string EscapeLikeWildcards(string word) => word
        .Replace("\\", "\\\\")
        .Replace("%", "\\%")
        .Replace("_", "\\_");
}
