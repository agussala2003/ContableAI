using ContableAI.Domain.Enums;

namespace ContableAI.Domain.Entities;

/// <summary>
/// Registro de auditoría generado automáticamente por <see cref="ContableAI.Infrastructure.Persistence.AuditInterceptor"/>
/// en cada operación de escritura sobre entidades de negocio.
/// </summary>
public class AuditLog
{
    public Guid       Id         { get; set; } = Guid.NewGuid();

    /// <summary>StudioTenantId del usuario que realizó la operación.</summary>
    public string     TenantId   { get; set; } = string.Empty;

    public string     UserId     { get; set; } = string.Empty;
    public string     UserEmail  { get; set; } = string.Empty;

    /// <summary>
    /// Tipo de operación: "Created", "Updated" o "Deleted".
    /// Usar <see cref="AuditAction"/> convertido a string para evitar magic strings.
    /// </summary>
    public string     Action     { get; set; } = AuditAction.Updated.ToString();

    /// <summary>Nombre de la entidad afectada (ej: "BankTransaction", "AccountingRule").</summary>
    public string     EntityName { get; set; } = string.Empty;
    public string     EntityId   { get; set; } = string.Empty;

    /// <summary>JSON con los valores modificados; null si no hay diff disponible.</summary>
    public string?    Changes    { get; set; }
    public DateTime   Timestamp  { get; set; } = DateTime.UtcNow;
}
