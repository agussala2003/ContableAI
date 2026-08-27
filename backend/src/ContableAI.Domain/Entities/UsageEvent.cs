using ContableAI.Domain.Enums;

namespace ContableAI.Domain.Entities;

/// <summary>
/// Un hecho de consumo facturable. Es el ledger de facturación: <b>append-only</b>.
///
/// Nunca se hace UPDATE ni DELETE sobre esta tabla. Una bonificación, un reverso o un ajuste se
/// registran como un evento NUEVO con <see cref="Quantity"/> negativa, de modo que el historial
/// siempre explique cómo se llegó al total facturado. Por eso las propiedades son <c>init</c>: un
/// asiento del ledger que se puede editar deja de ser evidencia.
///
/// <b>Por qué existe, teniendo ya <c>QuotaService</c>:</b> ese servicio cuenta el stock vivo
/// (<c>COUNT(*)</c> sobre los movimientos del mes) y sirve como tope de capacidad, pero es inútil
/// para facturar — si el usuario borra movimientos, el consumo "se devuelve". Son dos preguntas
/// distintas: <i>Quota</i> responde "¿puede?", <i>Usage</i> responde "¿cuánto consumió?".
///
/// <b>Por qué no se reutiliza <c>AuditLog</c>:</b> auditoría y facturación tienen retención y ciclo
/// de vida distintos (ver <c>docs/RETENCION_DATOS.md</c>). Mezclarlas haría que una política de
/// purga de auditoría borre plata.
/// </summary>
public class UsageEvent
{
    public Guid Id { get; private set; } = Guid.NewGuid();

    /// <summary>
    /// Estudio al que se le imputa el consumo. Es el ancla de aislamiento y la clave de facturación:
    /// se factura al estudio, no a la empresa. Es <c>string</c> —no <c>Guid</c>— por coherencia con
    /// el resto del dominio (ver <c>Company.StudioTenantId</c>, <c>BankTransaction.StudioTenantId</c>).
    /// </summary>
    public string StudioTenantId { get; init; } = string.Empty;

    /// <summary>
    /// Empresa que originó el consumo, cuando se conoce. Es NULLABLE a propósito: una carga puede
    /// entrar sin empresa (el bucket legacy), y el consumo igual hay que registrarlo. Solo sirve
    /// para desglosar el consumo por cliente del estudio; la facturación se hace por estudio.
    /// </summary>
    public Guid? CompanyId { get; init; }

    public UsageEventType Type { get; init; }

    /// <summary>
    /// Cuánto se consumió. Default 1 (un extracto). Admite negativos para registrar reversos y
    /// bonificaciones sin borrar el evento original.
    /// </summary>
    public int Quantity { get; init; } = 1;

    public DateTime OccurredAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Período de facturación en formato <c>YYYY-MM</c>. Se desnormaliza desde
    /// <see cref="OccurredAt"/> a propósito, y a diferencia de otras desnormalizaciones del sistema
    /// esta es segura: el evento es inmutable, así que el período no puede quedar desalineado.
    /// Evita que cada consulta de consumo tenga que hacer aritmética de fechas sobre la tabla
    /// entera, y deja el filtro como una comparación de igualdad indexable.
    /// </summary>
    public string PeriodKey { get; init; } = string.Empty;

    /// <summary>
    /// Identidad del hecho consumido, no del registro. Para un extracto es el SHA-256 de su
    /// contenido: resubir el mismo archivo —o el reintento automático de un job que falló a mitad
    /// de camino— produce la misma clave y no vuelve a cobrar.
    ///
    /// La garantía NO está en el código sino en el índice único
    /// <c>(StudioTenantId, Type, IdempotencyKey)</c>: un chequeo previo en C# tiene una ventana de
    /// carrera entre el SELECT y el INSERT que dos jobs de Hangfire concurrentes pueden atravesar.
    /// </summary>
    public string IdempotencyKey { get; init; } = string.Empty;

    /// <summary>Arma el <see cref="PeriodKey"/> de un instante. Único lugar donde se define el formato.</summary>
    public static string PeriodKeyOf(DateTime utc) => utc.ToString("yyyy-MM");
}
