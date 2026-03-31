using ContableAI.Domain.Constants;
using ContableAI.Domain.Enums;

namespace ContableAI.Domain.Entities;

/// <summary>
/// Transacción bancaria importada desde un extracto.
/// Se clasifica automáticamente por reglas y luego se asienta
/// mediante <see cref="JournalEntry"/>.
/// </summary>
public class BankTransaction : ITenantEntity
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public DateOnly Date { get; init; }
    public string Description { get; init; } = string.Empty;
    public decimal Amount { get; init; }
    public TransactionType Type { get; init; }
    public decimal BalanceAfter { get; init; }
    public string? SourceBank { get; init; }

    public string? AssignedAccount { get; private set; }
    public Guid? AppliedRuleId { get; private set; }
    public bool NeedsTaxMatching { get; private set; } = false;

    /// <summary>Identificador externo de la operación (ej: ID de operación MercadoPago).</summary>
    public string? ExternalId { get; set; }

    /// <summary>Posición dentro del lote de carga — preserva el orden de escaneo del PDF.</summary>
    public int SortOrder { get; set; } = 0;
    /// <summary>
    /// Origen de la clasificación. Ver <see cref="ClassificationSources"/> para los valores posibles.
    /// </summary>
    public string ClassificationSource { get; private set; } = ClassificationSources.Pending;

    /// <summary>Nivel de confianza de la clasificación: 0.0 (rojo) → 0.5 (amarillo) → 1.0 (verde).</summary>
    public float ConfidenceScore { get; private set; } = 0f;

    /// <summary>True cuando la descripción indica un pago de tarjeta que requiere desglose manual.</summary>
    public bool NeedsBreakdown { get; private set; } = false;

    /// <summary>True cuando otra transacción sindical con mismo importe y fecha cercana ya existe.</summary>
    public bool IsPossibleDuplicate { get; set; } = false;

    /// <summary>Notas adicionales del contador o del sistema (ej: F.931 – Período 202501).</summary>
    public string? Notes { get; set; }

    // Legacy string tenant — se mantiene por compatibilidad con datos existentes
    public string TenantId { get; set; } = "ESTUDIO_DEFAULT";

    // FK real a Company (reemplaza a TenantId a largo plazo)
    public Guid? CompanyId { get; set; }
    public Company? Company { get; set; }

    // FK al asiento contable generado (null = aún no asentado)
    public Guid? JournalEntryId { get; set; }

    /// <summary>
    /// Asigna una cuenta contable a la transacción, registrando el origen y la confianza.
    /// </summary>
    /// <param name="account">Nombre de la cuenta contable destino.</param>
    /// <param name="ruleId">ID de la regla aplicada, o <c>null</c> si fue clasificada por IA/manual.</param>
    /// <param name="needsTaxMatching">Si la clasificación requiere cruce con presentaciones AFIP.</param>
    /// <param name="source">Origen de la clasificación. Ver <see cref="ClassificationSources"/>.</param>
    /// <param name="confidenceScore">Confianza de 0 (rojo) a 1 (verde).</param>
    public void Assign(string account, Guid? ruleId = null, bool needsTaxMatching = false, string source = ClassificationSources.HardRule, float confidenceScore = 1.0f)
    {
        AssignedAccount      = account;
        AppliedRuleId        = ruleId;
        NeedsTaxMatching     = needsTaxMatching;
        ClassificationSource = source;
        ConfidenceScore      = Math.Clamp(confidenceScore, 0f, 1f);
    }

    /// <summary>Marca que este movimiento de tarjeta requiere desglose manual antes de asentar.</summary>
    public void MarkNeedsBreakdown()    => NeedsBreakdown      = true;

    /// <summary>Marca que existe otra transacción sindical con el mismo importe en fecha cercana.</summary>
    public void MarkPossibleDuplicate() => IsPossibleDuplicate  = true;
}