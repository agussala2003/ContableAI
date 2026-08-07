using ContableAI.Domain.Entities;

namespace ContableAI.Domain.Common;

/// <summary>
/// Única fuente de verdad para la firma de deduplicación de una <see cref="BankTransaction"/>.
///
/// La usa la subida de extractos tanto sobre los movimientos ya persistidos como sobre los
/// recién parseados: al construir ambos con este método se garantiza que la firma sea idéntica
/// (antes la interpolación estaba repetida en dos lugares del handler, con riesgo de divergencia
/// silenciosa que rompería la detección de duplicados).
///
/// La moneda forma parte de la firma: dos movimientos de igual importe/fecha en distinta moneda
/// (ej. 639,40 ARS vs 639,40 USD) no son duplicados. Cuando el movimiento trae un id externo de
/// la operación (ej. MercadoPago) este ancla la firma; si no, se usa fecha + descripción + importe.
///
/// La CUENTA BANCARIA también forma parte de la firma (F1.d). Sin ella, los dos extremos de una
/// transferencia entre cuentas propias —mismo importe, misma fecha, descripción casi idéntica, en
/// dos cuentas de la misma empresa— se descartarían silenciosamente como duplicados, perdiendo la
/// mitad del movimiento. Es exactamente el flujo que habilita el multi-cuenta, así que es el caso
/// que MÁS dispara el falso positivo.
/// </summary>
public static class TransactionSignatureBuilder
{
    /// <summary>Firma canónica del movimiento para detección de duplicados.</summary>
    public static string Build(BankTransaction tx)
    {
        // Los movimientos sin cuenta (legacy, o parseados antes de resolver el enrutamiento)
        // comparten un bucket propio: entre ellos la firma sigue comportándose como antes.
        var account = tx.BankAccountId?.ToString() ?? "NO_ACCOUNT";

        return tx.ExternalId != null
            ? $"ACC|{account}|EXT|{tx.ExternalId}|{tx.Date}|{tx.Amount}|{tx.Description}|{tx.Type}|{tx.Currency}"
            : $"ACC|{account}|{tx.Date}|{tx.Description}|{tx.Amount}|{tx.Type}|{tx.Currency}";
    }
}
