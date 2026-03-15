using ContableAI.Application.Common;
using ContableAI.Application.Features.Companies.Commands;
using MediatR;

namespace ContableAI.Application.Features.Companies.Queries;

// ── Get all companies for a studio ────────────────────────────────────────────
public record GetCompaniesQuery(string StudioTenantId)
    : IRequest<Result<List<CompanyResponse>>>;

// ── Get a single company ───────────────────────────────────────────────────────
public record GetCompanyQuery(Guid Id)
    : IRequest<Result<CompanyResponse>>;

// ── Get rules assigned to a company ───────────────────────────────────────────
public record GetCompanyRulesQuery(Guid CompanyId)
    : IRequest<Result<List<RuleResponse>>>;
