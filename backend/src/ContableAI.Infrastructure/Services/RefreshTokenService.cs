using ContableAI.Domain.Entities;
using ContableAI.Infrastructure.Options;
using ContableAI.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;

namespace ContableAI.Infrastructure.Services;

/// <summary>Resultado de una rotación exitosa: el usuario y el nuevo token opaco (en claro).</summary>
public sealed record RefreshRotationResult(User User, string NewRawToken);

/// <summary>
/// Emite, rota y revoca refresh tokens (A-3). El token opaco en claro solo existe en tránsito
/// (cookie HttpOnly); en la BD se guarda únicamente su hash SHA-256.
/// </summary>
public interface IRefreshTokenService
{
    /// <summary>Crea un refresh token para el usuario y devuelve el valor en claro (para la cookie).</summary>
    Task<string> IssueAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Valida y ROTA el token presentado: revoca el actual y emite uno nuevo. Devuelve el usuario
    /// y el nuevo token en claro, o <c>null</c> si el token es inválido/expirado/revocado o el
    /// usuario ya no puede operar. Ante reuso de un token ya rotado, revoca toda la cadena del usuario.
    /// </summary>
    Task<RefreshRotationResult?> RotateAsync(string rawToken, CancellationToken ct = default);

    /// <summary>Revoca el token presentado (logout). No falla si el token no existe o ya estaba revocado.</summary>
    Task RevokeAsync(string rawToken, CancellationToken ct = default);
}

public sealed class RefreshTokenService : IRefreshTokenService
{
    private readonly ContableAIDbContext _db;
    private readonly JwtOptions _jwt;
    private readonly ILogger<RefreshTokenService> _logger;

    public RefreshTokenService(
        ContableAIDbContext db,
        IOptions<JwtOptions> jwt,
        ILogger<RefreshTokenService> logger)
    {
        _db     = db;
        _jwt    = jwt.Value;
        _logger = logger;
    }

    public async Task<string> IssueAsync(Guid userId, CancellationToken ct = default)
    {
        var raw = GenerateRawToken();

        _db.RefreshTokens.Add(new RefreshToken
        {
            UserId    = userId,
            TokenHash = Hash(raw),
            ExpiresAt = DateTime.UtcNow.AddDays(Math.Max(1, _jwt.RefreshTokenDays)),
        });
        await _db.SaveChangesAsync(ct);

        return raw;
    }

    public async Task<RefreshRotationResult?> RotateAsync(string rawToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawToken)) return null;

        var hash    = Hash(rawToken);
        var current = await _db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);

        if (current is null) return null; // token desconocido

        // Reuse detection: presentar un token ya revocado (rotado) sugiere robo → revocar todo.
        if (current.RevokedAt is not null)
        {
            _logger.LogWarning(
                "[Auth] Reuso de refresh token detectado para el usuario {UserId}. Se revocan todas sus sesiones.",
                current.UserId);
            await RevokeAllActiveForUserAsync(current.UserId, ct);
            return null;
        }

        if (DateTime.UtcNow >= current.ExpiresAt) return null; // expirado

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == current.UserId, ct);
        if (user is null || !user.IsActive)
        {
            current.RevokedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return null;
        }

        // Rotación: emitir el nuevo y revocar el actual enlazándolo.
        var newRaw = GenerateRawToken();
        var replacement = new RefreshToken
        {
            UserId    = user.Id,
            TokenHash = Hash(newRaw),
            ExpiresAt = DateTime.UtcNow.AddDays(Math.Max(1, _jwt.RefreshTokenDays)),
        };
        _db.RefreshTokens.Add(replacement);

        current.RevokedAt         = DateTime.UtcNow;
        current.ReplacedByTokenId = replacement.Id;

        await _db.SaveChangesAsync(ct);
        return new RefreshRotationResult(user, newRaw);
    }

    public async Task RevokeAsync(string rawToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawToken)) return;

        var hash  = Hash(rawToken);
        var token = await _db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is { RevokedAt: null })
        {
            token.RevokedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
    }

    private async Task RevokeAllActiveForUserAsync(Guid userId, CancellationToken ct)
    {
        var active = await _db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        foreach (var t in active)
            t.RevokedAt = now;

        await _db.SaveChangesAsync(ct);
    }

    // 256 bits de entropía criptográfica; hex es URL-safe (apto para cookie sin encoding extra).
    private static string GenerateRawToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    private static string Hash(string raw) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw)));
}
