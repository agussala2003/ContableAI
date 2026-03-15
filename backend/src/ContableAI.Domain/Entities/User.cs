using ContableAI.Domain.Enums;

namespace ContableAI.Domain.Entities;

/// <summary>
/// Usuario del sistema. Puede ser un dueño de estudio (<see cref="UserRole.StudioOwner"/>),
/// un operador de carga de datos (<see cref="UserRole.DataEntry"/>) o el administrador del sistema.
/// </summary>
public class User
{
    public Guid   Id            { get; private set; } = Guid.NewGuid();
    public string Email         { get; set; } = string.Empty;
    public string DisplayName   { get; set; } = string.Empty;
    public string PasswordHash  { get; set; } = string.Empty;
    public UserRole Role        { get; set; } = UserRole.DataEntry;

    /// <summary>Identifica al estudio contable al que pertenece este usuario.</summary>
    public string StudioTenantId { get; set; } = string.Empty;

    /// <summary>Estado de la cuenta para control de acceso y monetización.</summary>
    public AccountStatus AccountStatus { get; set; } = AccountStatus.Pending;

    /// <summary>Plan de suscripción del estudio. Solo aplica a <see cref="UserRole.StudioOwner"/>.</summary>
    public StudioPlan Plan { get; set; } = StudioPlan.Free;

    public bool     IsActive  { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Token para reset de contraseña (UUID de 32 hex chars, expira en 1 hora).</summary>
    public string?  PasswordResetToken       { get; set; }
    public DateTime? PasswordResetTokenExpiry { get; set; }
}
