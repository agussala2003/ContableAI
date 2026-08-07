using ContableAI.Domain.Enums;

namespace ContableAI.Domain.Entities;

/// <summary>
/// Regla de clasificación automática de transacciones bancarias.
/// Si <see cref="CompanyId"/> es <c>null</c>, es una regla global; de lo contrario, es específica de empresa
/// y tiene mayor precedencia sobre las globales con el mismo keyword.
/// </summary>
public class AccountingRule
{
    public Guid Id { get; private set; } = Guid.NewGuid();

    /// <summary>Texto que debe estar contenido en la descripción del movimiento (case-insensitive).</summary>
    public string Keyword { get; init; } = string.Empty;

    /// <summary>Dirección del movimiento a la que aplica la regla; <c>null</c> = aplica a Débito y Crédito.</summary>
    public TransactionType? Direction { get; init; }

    /// <summary>Nombre de la cuenta contable destino.</summary>
    public string TargetAccount { get; init; } = string.Empty;

    /// <summary>Número de prioridad — cuanto menor, más prioritaria. Reglas de empresa usan prioridad alta.</summary>
    public int Priority { get; init; } = 100;

    /// <summary>Si es <c>true</c>, la transacción requiere cruce con presentaciones AFIP después de clasificar.</summary>
    public bool RequiresTaxMatching { get; init; } = false;

    /// <summary>
    /// <c>null</c> = regla global (aplica a todas las empresas).
    /// <c>Guid</c> = regla específica de empresa (sobreescribe la global para el mismo keyword).
    /// </summary>
    public Guid? CompanyId { get; init; } = null;

    /// <summary>
    /// Estudio propietario de la regla, DESNORMALIZADO desde <see cref="Company.StudioTenantId"/>
    /// (mismo patrón que <see cref="BankTransaction.StudioTenantId"/>): lo usa el Global Query
    /// Filter para aislar por tenant sin joinear a Companies en cada query de reglas — incluida la
    /// carga de reglas del pipeline de clasificación, que corre por cada archivo subido.
    ///
    /// Se estampa en TODA regla, no solo en las de estudio:
    ///   · regla de empresa  → <see cref="CompanyId"/> != null y el estudio de esa empresa;
    ///   · regla de estudio  → <see cref="CompanyId"/> == null y el estudio;
    ///   · regla de sistema  → ambos <c>null</c> (aplica a todos los estudios).
    /// El nivel (empresa / estudio / sistema) se sigue discriminando por <see cref="CompanyId"/>,
    /// así que estampar el estudio en las reglas de empresa no altera la precedencia.
    ///
    /// Es <c>string</c> y no <c>Guid?</c> para poder comparar directamente contra el tenant del
    /// usuario en el filtro global (evita un cast no traducible a SQL) y porque hay estudios
    /// legacy cuyo identificador no es un GUID (<c>ESTUDIO_DEFAULT</c>): parsearlo daba
    /// <c>null</c>, que es justamente el valor reservado para "regla de sistema".
    /// </summary>
    public string? StudioTenantId { get; set; } = null;

    /// <summary>Si es <c>true</c>, la regla está activa y se aplica. Si es <c>false</c>, se ignora.</summary>
    public bool IsActive { get; set; } = true;
}