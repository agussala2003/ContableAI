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

        if (await _db.Users.AnyAsync(u => u.Email == email, ct))
            return Result<RegisterResponse>.Conflict("Ya existe un usuario con ese email.");

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
            true,
            "Tu cuenta fue creada exitosamente. Un administrador la activará en las próximas 24 horas. Te notificaremos por email cuando esté lista."),
            202);
    }
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

        if (await _db.Users.AnyAsync(u => u.Email == email, ct))
            return Result<RegisterStudioResponse>.Conflict("Ya existe un usuario con ese email.");

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
            var token = Guid.NewGuid().ToString("N");
            user.PasswordResetToken       = token;
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
        var user = await _db.Users.FirstOrDefaultAsync(
            u => u.Email == cmd.Email.ToLower().Trim() &&
                 u.PasswordResetToken == cmd.Token, ct);

        if (user is null || user.PasswordResetTokenExpiry < DateTime.UtcNow)
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
