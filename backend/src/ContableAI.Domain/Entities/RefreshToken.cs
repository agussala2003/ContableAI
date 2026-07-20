namespace ContableAI.Domain.Entities;

/// <summary>
/// Refresh token de sesión (A-3). Permite renovar el JWT de acceso —de vida corta— sin que el
/// usuario tenga que reautenticarse, y habilita la revocación server-side (logout real).
///
/// Nunca se guarda el token en claro: se persiste solo su hash SHA-256. En cada uso se rota
/// (el viejo se marca revocado y se enlaza al nuevo vía <see cref="ReplacedByTokenId"/>), de modo
/// que presentar un token ya rotado delata un posible robo (reuse detection).
/// </summary>
public class RefreshToken
{
    public Guid Id { get; private set; } = Guid.NewGuid();

    /// <summary>Usuario dueño de la sesión.</summary>
    public Guid UserId { get; set; }

    /// <summary>Hash SHA-256 (hex) del token opaco entregado al cliente. El valor en claro nunca se persiste.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }

    /// <summary>Fecha de revocación (logout, rotación o compromiso). <c>null</c> = activo.</summary>
    public DateTime? RevokedAt { get; set; }

    /// <summary>Token que reemplazó a éste al rotar. Permite detectar reuso de un token ya rotado.</summary>
    public Guid? ReplacedByTokenId { get; set; }

    /// <summary>Activo = no revocado y no expirado.</summary>
    public bool IsActive => RevokedAt is null && DateTime.UtcNow < ExpiresAt;
}
