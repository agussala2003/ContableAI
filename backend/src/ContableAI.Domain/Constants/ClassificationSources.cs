namespace ContableAI.Domain.Constants;

/// <summary>
/// Valores posibles para <see cref="Entities.BankTransaction.ClassificationSource"/>.
/// Centraliza los literales para evitar magic strings dispersos en el código.
/// </summary>
public static class ClassificationSources
{
    /// <summary>Sin clasificar — estado inicial al importar una transacción.</summary>
    public const string Pending = "Pending";

    /// <summary>Clasificada por una regla contable configurada (empresa o global).</summary>
    public const string HardRule = "HardRule";

    /// <summary>Asignada manualmente por el contador.</summary>
    public const string Manual = "Manual";

    /// <summary>Clasificada por similitud semántica desde la memoria vectorial (Qdrant).</summary>
    [Obsolete("Not used in MVP")]
    public const string VectorMemory = "VectorMemory";

    /// <summary>Clasificada por el modelo de lenguaje (OpenAI).</summary>
    [Obsolete("Not used in MVP")]
    public const string Ai = "AI";

    /// <summary>Error en clasificación — cuenta fallback asignada automáticamente.</summary>
    public const string Error = "Error";

    /// <summary>
    /// Regla coincidente es Impuesto al Cheque y la empresa tiene <c>SplitChequeTax = true</c>.
    /// El generador de asientos creará dos líneas (50% Débitos / 50% Créditos Bancarios).
    /// </summary>
    public const string ChequeTaxSplit = "ChequeTaxSplit";
}
