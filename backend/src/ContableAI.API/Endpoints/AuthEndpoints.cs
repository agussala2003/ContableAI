using ContableAI.Application.Common;
using ContableAI.Application.Features.Auth.Commands;
using ContableAI.API.Common;
using ContableAI.Domain.Constants;
using ContableAI.Domain.Entities;
using ContableAI.Domain.Enums;
using ContableAI.Infrastructure.Persistence;
using ContableAI.Infrastructure.Services;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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
        app.MapPost("/api/auth/login", async (LoginCommand cmd, IMediator mediator) =>
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
            return Results.Ok(result.Value);
        })
        .AllowAnonymous()
        .RequireRateLimiting("auth")
        .WithName("Login")
        .WithTags("Autenticación")
        .WithSummary("Autenticar usuario y obtener un JWT.")
        .WithDescription("Valida credenciales y emite un JWT de 7 días. Retorna 403 con código ACCOUNT_PENDING o ACCOUNT_SUSPENDED según el estado de la cuenta.")
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

        app.MapPost("/api/auth/register-studio", async (RegisterStudioCommand cmd, IMediator mediator) =>
        {
            var result = await mediator.Send(cmd);
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
}

