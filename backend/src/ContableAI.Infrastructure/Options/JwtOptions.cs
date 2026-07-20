namespace ContableAI.Infrastructure.Options;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Key { get; set; } = string.Empty;
    public string Issuer { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;

    /// <summary>
    /// Vida útil del JWT de acceso, en minutos (A-3). Corta a propósito: si el token se filtra,
    /// la ventana de abuso es acotada. La sesión se mantiene viva con refresh tokens rotatorios.
    /// </summary>
    public int AccessTokenMinutes { get; set; } = 30;

    /// <summary>Vida útil del refresh token, en días (A-3).</summary>
    public int RefreshTokenDays { get; set; } = 7;

    /// <summary>
    /// [Obsoleto] Vida útil histórica en días. Se conserva por compatibilidad de configuración;
    /// el access token ahora usa <see cref="AccessTokenMinutes"/>.
    /// </summary>
    public int ExpirationDays { get; set; } = 7;
}
