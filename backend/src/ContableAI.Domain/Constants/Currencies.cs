namespace ContableAI.Domain.Constants;

/// <summary>
/// Monedas soportadas por el sistema, representadas con su código ISO 4217 de 3 letras.
/// Se usan strings (no enum) por coherencia con el resto del dominio (ver
/// <see cref="ClassificationSources"/> y <c>SourceBank</c>): el valor viaja tal cual desde el
/// parser hasta la API y el frontend sin mapeos, y agregar una moneda futura no requiere
/// migrar datos ni convertir valores de enum.
/// </summary>
public static class Currencies
{
    /// <summary>Peso argentino — moneda por defecto de todo movimiento y asiento.</summary>
    public const string Ars = "ARS";

    /// <summary>Dólar estadounidense.</summary>
    public const string Usd = "USD";

    /// <summary>Longitud fija de un código de moneda (ISO 4217). Usada por el mapeo EF.</summary>
    public const int CodeLength = 3;

    /// <summary>
    /// Indica si el código de moneda es uno de los soportados. Se valida en los puntos de
    /// entrada (parser, endpoints) para impedir que un valor no reconocido llegue a la BD.
    /// </summary>
    public static bool IsSupported(string? code) =>
        code is Ars or Usd;
}
