using ContableAI.Application.Common;
using MediatR;

namespace ContableAI.Application.Features.Auth.Commands;

// ── Login ─────────────────────────────────────────────────────────────────────
public record LoginCommand(string Email, string Password)
    : IRequest<Result<LoginResponse>>;

public record LoginResponse(
    string  Token,
    Guid    UserId,
    string  Email,
    string? DisplayName,
    string  Role,
    string  StudioTenantId);

// ── Register (invited user, pending approval) ─────────────────────────────────
public record RegisterCommand(
    string  Email,
    string  Password,
    string? DisplayName,
    string? StudioTenantId)
    : IRequest<Result<RegisterResponse>>;

public record RegisterResponse(bool PendingApproval, string Message);

// ── Register Studio (public self-serve, active immediately) ───────────────────
public record RegisterStudioCommand(
    string StudioName,
    string Email,
    string Password)
    : IRequest<Result<RegisterStudioResponse>>;

public record RegisterStudioResponse(
    string  Token,
    Guid    UserId,
    string  Email,
    string? DisplayName,
    string  StudioTenantId);

// ── Forgot Password ───────────────────────────────────────────────────────────
public record ForgotPasswordCommand(string Email)
    : IRequest<Result<ForgotPasswordResponse>>;

public record ForgotPasswordResponse(string Message);

// ── Reset Password ────────────────────────────────────────────────────────────
public record ResetPasswordCommand(string Token, string Email, string NewPassword)
    : IRequest<Result<ResetPasswordResponse>>;

public record ResetPasswordResponse(string Message);
