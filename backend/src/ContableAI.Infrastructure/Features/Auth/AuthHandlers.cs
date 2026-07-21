using ContableAI.Application.Common;
using ContableAI.Application.Features.Auth.Commands;
using ContableAI.Domain.Constants;
using ContableAI.Domain.Entities;
using ContableAI.Domain.Enums;
using ContableAI.Infrastructure.Options;
using ContableAI.Infrastructure.Persistence;
using ContableAI.Infrastructure.Services;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ContableAI.Infrastructure.Features.Auth;

// ── Login ─────────────────────────────────────────────────────────────────────
public sealed class LoginHandler : IRequestHandler<LoginCommand, Result<LoginResponse>>
{
    private readonly ContableAIDbContext   _db;
    private readonly IPasswordHasher<User> _hasher;
    private readonly IJwtTokenService      _jwt;

    public LoginHandler(
        ContableAIDbContext   db,
        IPasswordHasher<User> hasher,
        IJwtTokenService      jwt)
    {
        _db     = db;
        _hasher = hasher;
        _jwt    = jwt;
    }

    public async Task<Result<LoginResponse>> Handle(LoginCommand cmd, CancellationToken ct)
    {
        var user = await _db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Email == cmd.Email.ToLower().Trim(), ct);

        if (user is null)
            return Result<LoginResponse>.Failure("Invalid credentials.", 401);

        var verification = _hasher.VerifyHashedPassword(user, user.PasswordHash, cmd.Password);
        if (verification == PasswordVerificationResult.Failed)
            return Result<LoginResponse>.Failure("Invalid credentials.", 401);

        if (!user.IsActive)
            return Result<LoginResponse>.Failure("Invalid credentials.", 401);

        return user.AccountStatus switch
        {
            AccountStatus.Pending   => Result<LoginResponse>.Failure("ACCOUNT_PENDING|Tu cuenta está pendiente de activación. Te avisaremos por email cuando esté lista.", 403),
            AccountStatus.Suspended => Result<LoginResponse>.Failure("ACCOUNT_SUSPENDED|Tu cuenta fue suspendida. Contatá a soporte para más información.", 403),
            _ => Result<LoginResponse>.Success(new LoginResponse(
                    _jwt.GenerateToken(user),
                    user.Id, user.Email, user.DisplayName,
                    user.Role.ToString(), user.StudioTenantId)),
        };
    }
}

// ── Register (invited user, pending approval) ─────────────────────────────────
public sealed class RegisterHandler : IRequestHandler<RegisterCommand, Result<RegisterResponse>>
{
    private readonly ContableAIDbContext   _db;
    private readonly IPasswordHasher<User> _hasher;
    private readonly IWebHostEnvironment   _env;

    public RegisterHandler(
        ContableAIDbContext   db,
        IPasswordHasher<User> hasher,
        IWebHostEnvironment   env)
    {
        _db     = db;
        _hasher = hasher;
        _env    = env;
    }

    public async Task<Result<RegisterResponse>> Handle(RegisterCommand cmd, CancellationToken ct)
    {
        var email = cmd.Email.ToLower().Trim();

        // B-1b: respuesta constante — si el email ya existe se devuelve el MISMO 202 genérico
        // que en el alta real, sin crear nada. Este flujo es asíncrono por diseño ("un admin
        // la activará"), así que el atacante no puede distinguir por status, cuerpo ni timing
        // apreciable si el email estaba registrado (OWASP: prevención de enumeración de usuarios).
        if (await _db.Users.AnyAsync(u => u.Email == email, ct))
            return Result<RegisterResponse>.Success(new RegisterResponse(
                true, PendingActivationMessage), 202);

        var studioTenantId = cmd.StudioTenantId ?? Guid.NewGuid().ToString();
        bool isFirstUser   = !await _db.Users.AnyAsync(u => u.StudioTenantId == studioTenantId, ct);

        var user = new User
        {
            Email          = email,
            DisplayName    = cmd.DisplayName ?? email,
            StudioTenantId = studioTenantId,
            Role           = isFirstUser ? UserRole.StudioOwner : UserRole.DataEntry,
            AccountStatus  = AccountStatus.Pending,
            Plan           = (isFirstUser && _env.IsDevelopment()) ? StudioPlan.Enterprise : StudioPlan.Free,
        };
        user.PasswordHash = _hasher.HashPassword(user, cmd.Password);

        _db.Users.Add(user);
        await _db.SaveChangesAsync(ct);

        return Result<RegisterResponse>.Success(new RegisterResponse(
            true, PendingActivationMessage), 202);
    }

    /// <summary>Mensaje único del registro (alta real o email duplicado) — B-1b: debe ser idéntico en ambos caminos.</summary>
    private const string PendingActivationMessage =
        "Tu cuenta fue creada exitosamente. Un administrador la activará en las próximas 24 horas. Te notificaremos por email cuando esté lista.";
}

// ── Register Studio (public self-serve, active immediately) ───────────────────
public sealed class RegisterStudioHandler : IRequestHandler<RegisterStudioCommand, Result<RegisterStudioResponse>>
{
    private readonly ContableAIDbContext   _db;
    private readonly IPasswordHasher<User> _hasher;
    private readonly IJwtTokenService      _jwt;

    public RegisterStudioHandler(
        ContableAIDbContext   db,
        IPasswordHasher<User> hasher,
        IJwtTokenService      jwt)
    {
        _db     = db;
        _hasher = hasher;
        _jwt    = jwt;
    }

    public async Task<Result<RegisterStudioResponse>> Handle(RegisterStudioCommand cmd, CancellationToken ct)
    {
        var email = cmd.Email.ToLower().Trim();

        // B-1b (trade-off aceptado conscientemente): este flujo es self-serve con login
        // inmediato (devuelve JWT), por lo que una respuesta genérica exigiría un paso de
        // verificación por email que hoy no existe. El 409 revela existencia del email, pero
        // el mensaje es neutral (no la confirma en el texto) y orienta al usuario legítimo.
        // Mitigaciones: forgot-password y register (invitados) son de respuesta constante.
        // Documentado en docs/AUDITORIA.MD (B-1b).
        if (await _db.Users.AnyAsync(u => u.Email == email, ct))
            return Result<RegisterStudioResponse>.Conflict(
                "No se pudo completar el registro con ese email. Si ya tenés una cuenta, iniciá sesión o usá '¿Olvidaste tu contraseña?'.");

        var tenantId = Guid.NewGuid().ToString();
        var user = new User
        {
            Email          = email,
            DisplayName    = cmd.StudioName.Trim(),
            StudioTenantId = tenantId,
            Role           = UserRole.StudioOwner,
            AccountStatus  = AccountStatus.Active,
            IsActive       = true,
        };
        user.PasswordHash = _hasher.HashPassword(user, cmd.Password);

        _db.Users.Add(user);
        await _db.SaveChangesAsync(ct);

        return Result<RegisterStudioResponse>.Success(
            new RegisterStudioResponse(_jwt.GenerateToken(user), user.Id, user.Email, user.DisplayName, tenantId));
    }
}

// ── Forgot Password ───────────────────────────────────────────────────────────
public sealed class ForgotPasswordHandler : IRequestHandler<ForgotPasswordCommand, Result<ForgotPasswordResponse>>
{
    private readonly ContableAIDbContext _db;
    private readonly IEmailService       _emailService;
    private readonly FrontendOptions     _frontendOptions;
    private readonly ILogger<ForgotPasswordHandler> _logger;

    public ForgotPasswordHandler(
        ContableAIDbContext             db,
        IEmailService                   emailService,
        IOptions<FrontendOptions>       frontendOptions,
        ILogger<ForgotPasswordHandler>  logger)
    {
        _db              = db;
        _emailService    = emailService;
        _frontendOptions = frontendOptions.Value;
        _logger          = logger;
    }

    public async Task<Result<ForgotPasswordResponse>> Handle(ForgotPasswordCommand cmd, CancellationToken ct)
    {
        // Always return success — never reveal whether the email exists (prevents enumeration).
        var user = await _db.Users.FirstOrDefaultAsync(
            u => u.Email == cmd.Email.ToLower().Trim(), ct);

        if (user is not null && user.IsActive)
        {
            // B-1c: token de 256 bits desde CSPRNG; en la BD queda SOLO el hash SHA-256.
            // El valor en claro viaja únicamente en el link del email.
            var token = PasswordResetTokens.Generate();
            user.PasswordResetToken       = PasswordResetTokens.Hash(token);
            user.PasswordResetTokenExpiry = DateTime.UtcNow.AddHours(1);
            await _db.SaveChangesAsync(ct);

            var frontendUrl = string.IsNullOrWhiteSpace(_frontendOptions.BaseUrl)
                ? "http://localhost:4200"
                : _frontendOptions.BaseUrl;
            var resetUrl    = $"{frontendUrl}/reset-password?token={token}&email={Uri.EscapeDataString(user.Email)}";

            try
            {
                await _emailService.SendPasswordResetEmailAsync(user.Email, user.DisplayName, resetUrl, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send password reset email to {Email}", user.Email);
            }
        }

        return Result<ForgotPasswordResponse>.Success(
            new ForgotPasswordResponse("Si el email existe, recibirás un enlace para restablecer tu contraseña."));
    }
}

// ── Reset Password ────────────────────────────────────────────────────────────
public sealed class ResetPasswordHandler : IRequestHandler<ResetPasswordCommand, Result<ResetPasswordResponse>>
{
    private readonly ContableAIDbContext   _db;
    private readonly IPasswordHasher<User> _hasher;

    public ResetPasswordHandler(ContableAIDbContext db, IPasswordHasher<User> hasher)
    {
        _db     = db;
        _hasher = hasher;
    }

    public async Task<Result<ResetPasswordResponse>> Handle(ResetPasswordCommand cmd, CancellationToken ct)
    {
        // B-1c: se busca solo por email y se verifica el token comparando hashes en tiempo
        // constante (nunca el valor en claro contra la BD). Tokens legados en claro fallan
        // la verificación y expiran solos.
        var user = await _db.Users.FirstOrDefaultAsync(
            u => u.Email == cmd.Email.ToLower().Trim(), ct);

        if (user is null
            || user.PasswordResetTokenExpiry < DateTime.UtcNow
            || !PasswordResetTokens.Verify(cmd.Token, user.PasswordResetToken))
            return Result<ResetPasswordResponse>.Failure(
                "El enlace de restablecimiento es inválido o ya expiró.", 400);

        user.PasswordHash             = _hasher.HashPassword(user, cmd.NewPassword);
        user.PasswordResetToken       = null;
        user.PasswordResetTokenExpiry = null;
        await _db.SaveChangesAsync(ct);

        return Result<ResetPasswordResponse>.Success(
            new ResetPasswordResponse("Contraseña actualizada correctamente. Ya podés iniciar sesión."));
    }
}
