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
}