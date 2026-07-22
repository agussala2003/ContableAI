using System.Security.Cryptography;
using System.Text;

namespace ContableAI.Infrastructure.Services;

/// <summary>
/// Generación y verificación de tokens de recuperación de contraseña (B-1c). Mismo esquema que
/// los refresh tokens (A-3, <see cref="RefreshTokenService"/>): el valor en claro viaja solo en
/// el link del email; en la BD se persiste únicamente su hash SHA-256, de modo que un dump de la
/// tabla <c>Users</c> no permite blanquear contraseñas ajenas.
/// </summary>
internal static class PasswordResetTokens
{
    /// <summary>
    /// Token opaco de 256 bits desde el CSPRNG del sistema (supera los 128 bits mínimos de
    /// OWASP). Hex de 64 chars, apto para query string sin escaping.
    /// </summary>
    internal static string Generate() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    /// <summary>Hash SHA-256 (hex) del token — lo único que se persiste en <c>User.PasswordResetToken</c>.</summary>
    internal static string Hash(string rawToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));

    /// <summary>
    /// Compara en tiempo constante el token presentado contra el hash almacenado. Un token
    /// legado en claro (guid de 32 chars, pre B-1c) difiere en longitud y falla sin excepción:
    /// el usuario simplemente pide un nuevo link.
    /// </summary>
    internal static bool Verify(string rawToken, string? storedHash)
    {
        if (string.IsNullOrEmpty(rawToken) || string.IsNullOrEmpty(storedHash))
            return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(Hash(rawToken)),
            Encoding.UTF8.GetBytes(storedHash));
    }
}
