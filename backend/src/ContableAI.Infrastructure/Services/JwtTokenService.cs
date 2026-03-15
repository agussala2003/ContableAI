using ContableAI.Domain.Entities;
using ContableAI.Infrastructure.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Options;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace ContableAI.Infrastructure.Services;

/// <summary>Contrato para generación de tokens JWT de autenticación.</summary>
public interface IJwtTokenService
{
    /// <summary>
    /// Genera un JWT firmado con las claims del usuario.
    /// El token expira en 7 días.
    /// </summary>
    string GenerateToken(User user);
}

/// <summary>
/// Implementación de <see cref="IJwtTokenService"/> basada en HMAC-SHA256.
/// Lee la clave, issuer y audience desde la configuración (<c>Jwt:Key</c>, <c>Jwt:Issuer</c>, <c>Jwt:Audience</c>).
/// </summary>
public class JwtTokenService : IJwtTokenService
{
    private readonly JwtOptions _jwtOptions;

    public JwtTokenService(IOptions<JwtOptions> jwtOptions) => _jwtOptions = jwtOptions.Value;

    public string GenerateToken(User user)
    {
        var key     = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtOptions.Key));
        var creds   = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub,   user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim(JwtRegisteredClaimNames.Jti,   Guid.NewGuid().ToString()),
            new Claim("studioTenantId",              user.StudioTenantId),
            new Claim("displayName",                 user.DisplayName),
            new Claim(ClaimTypes.Role,               user.Role.ToString()),
        };

        var token = new JwtSecurityToken(
            issuer:            _jwtOptions.Issuer,
            audience:          _jwtOptions.Audience,
            claims:            claims,
            expires:           DateTime.UtcNow.AddDays(Math.Max(1, _jwtOptions.ExpirationDays)),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
