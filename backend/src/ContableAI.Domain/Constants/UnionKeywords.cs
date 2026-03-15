namespace ContableAI.Domain.Constants;

/// <summary>
/// Palabras clave que identifican movimientos relacionados con sindicatos en descripciones bancarias.
/// Se usan para detectar posibles duplicados en la carga de extractos.
/// </summary>
public static class UnionKeywords
{
    /// <summary>Keywords de sindicatos que deben consultarse para detección de duplicados.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        "UTGRA",
        "UOCRA",
        "FATSA",
        "SMATA",
        "UOM",
        "UPCN",
        "SINDICATO",
        "CUOTA SINDICAL",
        "CUOTA GREMIAL",
        "APORTE SINDICAL",
    ];
}
