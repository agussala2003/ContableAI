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
    ///
    /// CONSUME saldo: su <c>Quantity</c> es POSITIVA (+1 por extracto) y se RESTA del saldo.
    /// </summary>
    StatementProcessed = 0,

    /// <summary>
    /// Carga de saldo prepago hecha a mano por un administrador tras cobrar un pack de extractos.
    /// No hay pasarela de pagos: el evento se registra cuando el pago ya está confirmado, y su
    /// <c>IdempotencyKey</c> es la referencia del comprobante.
    ///
    /// APORTA saldo: su <c>Quantity</c> es positiva y se SUMA.
    /// </summary>
    StatementQuotaTopUp = 1,
}
