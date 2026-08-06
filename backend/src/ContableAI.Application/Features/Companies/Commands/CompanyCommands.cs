using ContableAI.Application.Common;
using MediatR;

namespace ContableAI.Application.Features.Companies.Commands;

// ── Create Company ─────────────────────────────────────────────────────────────
public record CreateCompanyCommand(
    string  Name,
    string  Cuit,
    string? BusinessType,
    string? BankAccountName,
    string  StudioTenantId,
    string? UsdBankAccountName = null)
    : IRequest<Result<CompanyResponse>>;

// ── Update Company ─────────────────────────────────────────────────────────────
public record UpdateCompanyCommand(
    Guid    Id,
    string? Name,
    string? BusinessType,
    bool?   SplitChequeTax,
    string? BankAccountName,
    string? UsdBankAccountName = null)
    : IRequest<Result<CompanyResponse>>;

// ── Delete Company (soft-delete) ───────────────────────────────────────────────
public record DeleteCompanyCommand(Guid Id)
    : IRequest<Result<DeletedResponse>>;

// ── Create accounting rule for a company ──────────────────────────────────────
public record CreateCompanyRuleCommand(
    Guid    CompanyId,
    string  Keyword,
    string  TargetAccount,
    string? Direction,
    int?    Priority,
    bool?   RequiresTaxMatching,
    string  StudioTenantId)
    : IRequest<Result<RuleResponse>>;

// ── Create accounting rule at studio level ────────────────────────────────────
public record CreateStudioRuleCommand(
    string  StudioTenantId,
    string  Keyword,
    string  TargetAccount,
    string? Direction,
    int?    Priority,
    bool?   RequiresTaxMatching)
    : IRequest<Result<RuleResponse>>;

// ── Shared response types ──────────────────────────────────────────────────────
public record CompanyResponse(
    Guid    Id,
    string  Name,
    string  Cuit,
    string  BusinessType,
    bool    SplitChequeTax,
    string? BankAccountName,
    string  StudioTenantId,
    string? UsdBankAccountName = null);

public record RuleResponse(
    Guid    Id,
    Guid?   CompanyId,
    string? StudioTenantId,
    string  Keyword,
    string  TargetAccount,
    string? Direction,
    int     Priority,
    bool    RequiresTaxMatching,
    bool    IsActive);

public record DeletedResponse(string Message);
