using ContableAI.Application.Common;
using ContableAI.Application.Features.Auth.Commands;
using ContableAI.API.Common;
using ContableAI.Domain.Constants;
using ContableAI.Domain.Entities;
using ContableAI.Domain.Enums;
using ContableAI.Infrastructure.Options;
using ContableAI.Infrastructure.Persistence;
using ContableAI.Infrastructure.Services;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ContableAI.API.Endpoints;

internal sealed record SeedAdminRequest(string AdminPassword);

/// <summary>Handles the one-time system admin bootstrap.</summary>
internal static class SeedAdminHandler
{
    internal static async Task<IResult> Handle(
        SeedAdminRequest      request,
        IPasswordHasher<User> hasher,
        IJwtTokenService      jwt,
        ContableAIDbContext   db)
    {
        if (string.IsNullOrWhiteSpace(request.AdminPassword) || request.AdminPassword.Length < 12)
            return Results.BadRequest("AdminPassword debe tener al menos 12 caracteres.");

        if (await db.Users.AnyAsync(u => u.Role == UserRole.SystemAdmin))
            return Results.Conflict("Ya existe un administrador del sistema. Este endpoint está deshabilitado.");

        const string adminEmail = "admin@contableai.com";

        var user = new User
        {
            Email          = adminEmail,
            DisplayName    = "Admin ContableAI",
            StudioTenantId = TenantConstants.System,
            Role           = UserRole.SystemAdmin,
            AccountStatus  = AccountStatus.Active,
            IsActive       = true,
        };
        user.PasswordHash = hasher.HashPassword(user, request.AdminPassword);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        return Results.Ok(new
        {
            Message = "Administrador creado exitosamente.",
            Email   = adminEmail,
            Token   = jwt.GenerateToken(user),
        });
    }
}

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        app.MapPost("/api/auth/login", async (
            LoginCommand          cmd,
            IMediator             mediator,
            HttpContext           httpCtx,
            IRefreshTokenService  refreshTokens,
            IWebHostEnvironment   env,
            IOptions<JwtOptions>  jwtOptions) =>
        {
            var result = await mediator.Send(cmd);
            if (!result.IsSuccess)
            {
                // Detect pending/suspended codes encoded as "CODE|Message"
                var parts = result.Error?.Split('|', 2);
                if (parts?.Length == 2)
                    return Results.Json(new { Code = parts[0], Message = parts[1] }, statusCode: result.StatusCode);
                return result.ToHttpResult();
            }

            // A-3: emitir refresh token en cookie HttpOnly (fuera del alcance de JS).
            await IssueRefreshCookieAsync(httpCtx, refreshTokens, env, jwtOptions.Value, result.Value!.UserId);
            return Results.Ok(result.Value);
        })
        .AllowAnonymous()
        .RequireRateLimiting("auth")
        .WithName("Login")
        .WithTags("Autenticación")
        .WithSummary("Autenticar usuario y obtener un JWT de acceso de vida corta.")
        .WithDescription("Valida credenciales, emite un JWT de acceso (~30 min) en el body y un refresh token en cookie HttpOnly. Retorna 403 con código ACCOUNT_PENDING o ACCOUNT_SUSPENDED según el estado de la cuenta.")
        .Produces<LoginResponse>(200)
        .Produces<ProblemDetails>(401)
        .Produces<ProblemDetails>(403);

        app.MapPost("/api/auth/register", async (RegisterCommand cmd, IMediator mediator) =>
        {
            var result = await mediator.Send(cmd);
            if (!result.IsSuccess) return result.ToHttpResult();
            return Results.Accepted((string?)null, result.Value);
        })
        .AllowAnonymous()
        .RequireRateLimiting("auth")
        .WithName("Register")
        .WithTags("Autenticación")
        .WithSummary("Registrar un nuevo usuario (queda pendiente de aprobación).")
        .WithDescription("Crea una cuenta en estado Pending. Un StudioOwner del estudio debe activarla. Body: { email, password, displayName, studioTenantId }.")
        .Produces<RegisterResponse>(202)
        .Produces<ProblemDetails>(409);

        app.MapPost("/api/auth/register-studio", async (
            RegisterStudioCommand cmd,
            IMediator             mediator,
            HttpContext           httpCtx,
            IRefreshTokenService  refreshTokens,
            IWebHostEnvironment   env,
            IOptions<JwtOptions>  jwtOptions) =>
        {
            var result = await mediator.Send(cmd);
            if (result.IsSuccess)
                await IssueRefreshCookieAsync(httpCtx, refreshTokens, env, jwtOptions.Value, result.Value!.UserId);
            return result.ToHttpResult();
        })
        .AllowAnonymous()
        .RequireRateLimiting("auth")
        .WithName("RegisterStudio")
        .WithTags("Autenticación")
        .WithSummary("Registrar un nuevo estudio contable (cuenta activa de inmediato).")
        .WithDescription("Crea un nuevo estudio con un tenantId propio. El registrante queda como StudioOwner con la cuenta activa. Body: { studioName, email, password }.")
        .Produces<RegisterStudioResponse>(200)
        .Produces<ProblemDetails>(409);

        app.MapPost("/api/auth/forgot-password", async (ForgotPasswordCommand cmd, IMediator mediator) =>
        {
            var result = await mediator.Send(cmd);
            return Results.Ok(result.Value);
        })
        .AllowAnonymous()
        .RequireRateLimiting("auth")
        .WithName("ForgotPassword")
        .WithTags("Autenticación")
        .WithSummary("Solicitar link de recuperación de contraseña.")
        .WithDescription("Siempre devuelve 200 para evitar enumeración de emails. Envía un link válido por 1 hora a la dirección indicada. Body: { email }.")
        .Produces<ForgotPasswordResponse>(200);

        app.MapPost("/api/auth/reset-password", async (ResetPasswordCommand cmd, IMediator mediator) =>
        {
            var result = await mediator.Send(cmd);
            return result.ToHttpResult();
        })
        .AllowAnonymous()
        .RequireRateLimiting("auth")
        .WithName("ResetPassword")
        .WithTags("Autenticación")
        .WithSummary("Restablecer contraseña con el token recibido por email.")
        .WithDescription("Body: { token, email, newPassword }. El token expira en 1 hora una vez generado.")
        .Produces<ResetPasswordResponse>(200)
        .Produces<ProblemDetails>(400);

        // ── Refresh (A-3): rota el refresh token de la cookie y emite un nuevo access token ──
        app.MapPost("/api/auth/refresh", async (
            HttpContext           httpCtx,
            IRefreshTokenService  refreshTokens,
            IJwtTokenService      jwt,
            IWebHostEnvironment   env,
            IOptions<JwtOptions>  jwtOptions) =>
        {
            var presented = httpCtx.Request.Cookies[RefreshCookieName];
            var rotation  = await refreshTokens.RotateAsync(presented ?? string.Empty, httpCtx.RequestAborted);

            if (rotation is null)
            {
                ClearRefreshCookie(httpCtx, env);
                return Results.Json(new { message = "Sesión inválida o expirada." }, statusCode: 401);
            }

            // Rotación: nueva cookie con el token rotado + nuevo access token en el body.
            httpCtx.Response.Cookies.Append(
                RefreshCookieName, rotation.NewRawToken, BuildCookieOptions(env, jwtOptions.Value));

            var u = rotation.User;
            return Results.Ok(new LoginResponse(
                jwt.GenerateToken(u), u.Id, u.Email, u.DisplayName, u.Role.ToString(), u.StudioTenantId));
        })
        .AllowAnonymous()
        .RequireRateLimiting("auth")
        .WithName("Refresh")
        .WithTags("Autenticación")
        .WithSummary("Renovar el JWT de acceso usando el refresh token (cookie HttpOnly).")
        .WithDescription("Lee el refresh token de la cookie, lo rota (revoca el anterior) y devuelve un nuevo JWT de acceso. Retorna 401 si la cookie es inválida, expiró o fue revocada.")
        .Produces<LoginResponse>(200)
        .Produces(401);

        // ── Logout (A-3): revoca el refresh token y limpia la cookie ─────────────────
        app.MapPost("/api/auth/logout", async (
            HttpContext          httpCtx,
            IRefreshTokenService refreshTokens,
            IWebHostEnvironment  env) =>
        {
            var presented = httpCtx.Request.Cookies[RefreshCookieName];
            if (!string.IsNullOrEmpty(presented))
                await refreshTokens.RevokeAsync(presented, httpCtx.RequestAborted);

            ClearRefreshCookie(httpCtx, env);
            return Results.Ok(new { message = "Sesión cerrada." });
        })
        // Anónimo a propósito: debe funcionar aunque el access token ya haya expirado.
        .AllowAnonymous()
        .WithName("Logout")
        .WithTags("Autenticación")
        .WithSummary("Cerrar sesión: revoca el refresh token y borra la cookie.")
        .Produces(200);

        if (app.Environment.IsDevelopment())
        {
            // ── Seed admin — only available in local development ─────────────
            app.MapPost("/api/auth/seed-admin", SeedAdminHandler.Handle)
            .AllowAnonymous()
            .WithName("SeedAdmin")
            .WithTags("Autenticación")
            .WithSummary("Bootstrap único del administrador del sistema.")
            .WithDescription("Solo Development: crea el usuario SystemAdmin inicial (admin@contableai.com). Requiere body { adminPassword } con mínimo 12 caracteres. Se auto-deshabilita en cuanto ya existe un SystemAdmin.")
            .Produces(200)
            .Produces<ProblemDetails>(409);
        }
    }

    // ── Refresh token cookie (A-3) ───────────────────────────────────────────────
    private const string RefreshCookieName = "contableai_refresh";

    private static async Task IssueRefreshCookieAsync(
        HttpContext ctx, IRefreshTokenService refreshTokens,
        IWebHostEnvironment env, JwtOptions jwt, Guid userId)
    {
        var raw = await refreshTokens.IssueAsync(userId, ctx.RequestAborted);
        ctx.Response.Cookies.Append(RefreshCookieName, raw, BuildCookieOptions(env, jwt));
    }

    /// <summary>
    /// Opciones de la cookie del refresh token. HttpOnly (nunca visible a JS). En producción
    /// Secure + SameSite=None porque el front (Vercel) y la API (Render) son cross-site; en
    /// desarrollo local SameSite=Lax sin Secure (http://localhost). Path acotado a /api/auth.
    /// </summary>
    private static CookieOptions BuildCookieOptions(IWebHostEnvironment env, JwtOptions jwt) => new()
    {
        HttpOnly    = true,
        Secure      = !env.IsDevelopment(),
        SameSite    = env.IsDevelopment() ? SameSiteMode.Lax : SameSiteMode.None,
        Path        = "/api/auth",
        Expires     = DateTimeOffset.UtcNow.AddDays(Math.Max(1, jwt.RefreshTokenDays)),
        IsEssential = true,
    };

    private static void ClearRefreshCookie(HttpContext ctx, IWebHostEnvironment env) =>
        ctx.Response.Cookies.Delete(RefreshCookieName, new CookieOptions
        {
            HttpOnly = true,
            Secure   = !env.IsDevelopment(),
            SameSite = env.IsDevelopment() ? SameSiteMode.Lax : SameSiteMode.None,
            Path     = "/api/auth",
        });
}

