namespace ContableAI.Domain.Enums;

/// <summary>
/// Operación registrada en el log de auditoría.
/// </summary>
public enum AuditAction
{
    /// <summary>Entidad creada.</summary>
    Created,

    /// <summary>Entidad modificada.</summary>
    Updated,

    /// <summary>Entidad eliminada.</summary>
    Deleted,
}
