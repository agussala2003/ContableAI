using ContableAI.Application.Common;
using MediatR;

namespace ContableAI.Application.Features.Rules.Commands;

/// <summary>
/// Reaplicación forzada de una regla sobre los movimientos históricos de su empresa.
///
/// A diferencia del <c>POST /api/rules/{id}/reapply</c> clásico —que solo completa movimientos sin
/// categorizar— esta operación SOBRESCRIBE la cuenta asignada, incluida la puesta a mano por el
/// contador. Por eso siempre se ofrece primero en modo <paramref name="DryRun"/>: el mismo comando
/// calcula el impacto sin escribir, y la UI lo muestra antes de pedir confirmación.
///
/// Con <paramref name="DryRun"/> en <c>false</c> se ejecuta como job de Hangfire: sobre años de
/// historia el lote puede ser de decenas de miles de movimientos y no entra en el tiempo de una
/// request HTTP.
/// </summary>
public sealed record ReapplyRuleCommand(
    Guid    RuleId,
    string  StudioTenantId,
    string  RequestedByEmail,
    bool    DryRun) : IRequest<Result<ReapplyRuleReport>>;

/// <summary>
/// Impacto de la reaplicación, con el desglose por origen previo de la clasificación.
/// <paramref name="Manual"/> es el que dispara la advertencia destructiva en la UI: son
/// asignaciones hechas a mano que la regla va a pisar.
/// </summary>
public sealed record ReapplyRuleReport(
    Guid    RuleId,
    string  Keyword,
    string  TargetAccount,
    bool    DryRun,

    /// <summary>Movimientos que la operación va a modificar (o modificó).</summary>
    int     TotalToUpdate,

    /// <summary>De los anteriores: estaban sin categorizar.</summary>
    int     Pending,
    /// <summary>De los anteriores: los había clasificado otra regla.</summary>
    int     ByOtherRule,
    /// <summary>De los anteriores: los había asignado el contador a mano.</summary>
    int     Manual,

    /// <summary>Coinciden con la regla pero ya tienen asiento generado: no se tocan.</summary>
    int     SkippedSettled,
    /// <summary>Coinciden pero caen en un período contable cerrado: no se tocan.</summary>
    int     SkippedClosedPeriod,
    /// <summary>Coinciden pero vienen de un cruce múltiple AFIP: pisarlos rompería el desglose por impuesto.</summary>
    int     SkippedAfipCombo,
    /// <summary>Coinciden y ya están exactamente como los dejaría esta regla: no se reescriben.</summary>
    int     AlreadyApplied);
