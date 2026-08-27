namespace ContableAI.Domain.Enums;

/// <summary>
/// Unidad de consumo que registra el ledger de facturación.
///
/// Se guarda como <c>int</c>, así que los valores existentes NO se pueden reordenar ni reasignar:
/// un evento ya registrado quedaría reinterpretado como otro tipo de consumo, y el ledger dejaría
/// de reflejar lo que realmente pasó. Un tipo nuevo se agrega siempre con el siguiente número.
/// </summary>
public enum UsageEventType
{
    /// <summary>
    /// Un extracto bancario procesado: el archivo se parseó y produjo al menos un movimiento.
    /// Es la unidad que se factura (ver el análisis de extracto vs. asiento): está alineada con el
    /// costo real —el parseo/OCR es lo único que escala con el uso— y tiene un punto de medición
    /// único en el pipeline de subida.
    /// </summary>
    StatementProcessed = 0,
}
