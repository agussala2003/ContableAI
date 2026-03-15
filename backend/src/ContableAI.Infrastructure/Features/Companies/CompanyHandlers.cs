using ContableAI.Application.Common;
using ContableAI.Application.Features.Companies.Commands;
using ContableAI.Application.Features.Companies.Queries;
using ContableAI.Domain.Entities;
using ContableAI.Domain.Enums;
using ContableAI.Infrastructure.Persistence;
using ContableAI.Infrastructure.Services;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace ContableAI.Infrastructure.Features.Companies;

// ── Get Companies ──────────────────────────────────────────────────────────────
public sealed class GetCompaniesHandler
    : IRequestHandler<GetCompaniesQuery, Result<List<CompanyResponse>>>
{
    private readonly ContableAIDbContext _db;
    public GetCompaniesHandler(ContableAIDbContext db) => _db = db;

    public async Task<Result<List<CompanyResponse>>> Handle(GetCompaniesQuery q, CancellationToken ct)
    {
        var items = await _db.Companies
            .AsNoTracking()
            .Where(c => c.IsActive && c.StudioTenantId == q.StudioTenantId)
            .OrderBy(c => c.Name)
            .ToListAsync(ct);

        var companies = items.Select(Projections.ToResponse).ToList();

        return Result<List<CompanyResponse>>.Success(companies);
    }
}

// ── Get Single Company ─────────────────────────────────────────────────────────
public sealed class GetCompanyHandler
    : IRequestHandler<GetCompanyQuery, Result<CompanyResponse>>
{
    private readonly ContableAIDbContext _db;
    public GetCompanyHandler(ContableAIDbContext db) => _db = db;

    public async Task<Result<CompanyResponse>> Handle(GetCompanyQuery q, CancellationToken ct)
    {
        var company = await _db.Companies
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == q.Id, ct);

        return company is null
            ? Result<CompanyResponse>.NotFound("Company not found.")
            : Result<CompanyResponse>.Success(Projections.ToResponse(company));
    }
}

// ── Get Company Rules ──────────────────────────────────────────────────────────
public sealed class GetCompanyRulesHandler
    : IRequestHandler<GetCompanyRulesQuery, Result<List<RuleResponse>>>
{
    private readonly ContableAIDbContext _db;
    public GetCompanyRulesHandler(ContableAIDbContext db) => _db = db;

    public async Task<Result<List<RuleResponse>>> Handle(GetCompanyRulesQuery q, CancellationToken ct)
    {
        var rules = await _db.AccountingRules
            .AsNoTracking()
            .Where(r => r.CompanyId == q.CompanyId || r.CompanyId == null)
            .OrderBy(r => r.CompanyId == null ? 1 : 0)
            .ThenBy(r => r.Priority)
            .ToListAsync(ct);

        var ruleResponses = rules.Select(Projections.ToRuleResponse).ToList();

        return Result<List<RuleResponse>>.Success(ruleResponses);
    }
}

// ── Create Company ─────────────────────────────────────────────────────────────
public sealed class CreateCompanyHandler
    : IRequestHandler<CreateCompanyCommand, Result<CompanyResponse>>
{
    private readonly ContableAIDbContext _db;
    private readonly IQuotaService       _quota;

    public CreateCompanyHandler(ContableAIDbContext db, IQuotaService quota)
    {
        _db    = db;
        _quota = quota;
    }

    public async Task<Result<CompanyResponse>> Handle(CreateCompanyCommand cmd, CancellationToken ct)
    {
        if (await _db.Companies.AnyAsync(c => c.Cuit == cmd.Cuit, ct))
            return Result<CompanyResponse>.Conflict($"Ya existe una empresa con CUIT {cmd.Cuit}.");

        if (!await _quota.CanAddCompanyAsync(cmd.StudioTenantId))
            return Result<CompanyResponse>.PaymentRequired(
                "QUOTA_EXCEEDED",
                "Alcanzaste el límite de empresas de tu plan. Actualizá a Pro para agregar más.");

        var company = new Company
        {
            Name            = cmd.Name,
            Cuit            = cmd.Cuit,
            BusinessType    = cmd.BusinessType ?? "GENERAL",
            BankAccountName = cmd.BankAccountName ?? string.Empty,
            StudioTenantId  = cmd.StudioTenantId,
        };

        _db.Companies.Add(company);
        await _db.SaveChangesAsync(ct);
        return Result<CompanyResponse>.Success(Projections.ToResponse(company), 201);
    }
}

// ── Update Company ─────────────────────────────────────────────────────────────
public sealed class UpdateCompanyHandler
    : IRequestHandler<UpdateCompanyCommand, Result<CompanyResponse>>
{
    private readonly ContableAIDbContext _db;
    public UpdateCompanyHandler(ContableAIDbContext db) => _db = db;

    public async Task<Result<CompanyResponse>> Handle(UpdateCompanyCommand cmd, CancellationToken ct)
    {
        var company = await _db.Companies.FindAsync([cmd.Id], ct);
        if (company is null)
            return Result<CompanyResponse>.NotFound();

        if (!string.IsNullOrWhiteSpace(cmd.Name))         company.Name = cmd.Name;
        if (!string.IsNullOrWhiteSpace(cmd.BusinessType)) company.BusinessType = cmd.BusinessType;
        if (cmd.SplitChequeTax.HasValue)                  company.SplitChequeTax = cmd.SplitChequeTax.Value;
        if (cmd.BankAccountName is not null)               company.BankAccountName = cmd.BankAccountName;

        await _db.SaveChangesAsync(ct);
        return Result<CompanyResponse>.Success(Projections.ToResponse(company));
    }
}

// ── Delete Company (soft-delete) ───────────────────────────────────────────────
public sealed class DeleteCompanyHandler
    : IRequestHandler<DeleteCompanyCommand, Result<DeletedResponse>>
{
    private readonly ContableAIDbContext _db;
    public DeleteCompanyHandler(ContableAIDbContext db) => _db = db;

    public async Task<Result<DeletedResponse>> Handle(DeleteCompanyCommand cmd, CancellationToken ct)
    {
        var company = await _db.Companies.FindAsync([cmd.Id], ct);
        if (company is null)
            return Result<DeletedResponse>.NotFound();

        company.IsActive = false;
        await _db.SaveChangesAsync(ct);
        return Result<DeletedResponse>.Success(new DeletedResponse("Company deactivated."), 204);
    }
}

// ── Create Company Rule ────────────────────────────────────────────────────────
public sealed class CreateCompanyRuleHandler
    : IRequestHandler<CreateCompanyRuleCommand, Result<RuleResponse>>
{
    private readonly ContableAIDbContext _db;
    private readonly IQuotaService       _quota;

    public CreateCompanyRuleHandler(ContableAIDbContext db, IQuotaService quota)
    {
        _db    = db;
        _quota = quota;
    }

    public async Task<Result<RuleResponse>> Handle(CreateCompanyRuleCommand cmd, CancellationToken ct)
    {
        var company = await _db.Companies.FindAsync([cmd.CompanyId], ct);
        if (company is null)
            return Result<RuleResponse>.NotFound("Company not found.");

        if (!await _quota.CanAddRuleAsync(cmd.StudioTenantId, cmd.CompanyId))
            return Result<RuleResponse>.PaymentRequired(
                "QUOTA_EXCEEDED",
                "Alcanzaste el límite de reglas por empresa de tu plan. Actualizá a Pro para agregar más.");

        TransactionType? direction = cmd.Direction?.ToUpper() switch
        {
            "DEBIT"  => TransactionType.Debit,
            "CREDIT" => TransactionType.Credit,
            _        => null,
        };

        var rule = new AccountingRule
        {
            Keyword             = cmd.Keyword,
            Direction           = direction,
            TargetAccount       = cmd.TargetAccount,
            Priority            = cmd.Priority ?? 100,
            RequiresTaxMatching = cmd.RequiresTaxMatching ?? false,
            CompanyId           = cmd.CompanyId,
        };

        _db.AccountingRules.Add(rule);
        await _db.SaveChangesAsync(ct);
        return Result<RuleResponse>.Success(Projections.ToRuleResponse(rule), 201);
    }
}

// ── Projection helpers ─────────────────────────────────────────────────────────
file static class Projections
{
    internal static CompanyResponse ToResponse(Company c) => new(
        c.Id, c.Name, c.Cuit, c.BusinessType, c.SplitChequeTax, c.BankAccountName, c.StudioTenantId);

    internal static RuleResponse ToRuleResponse(AccountingRule r) => new(
        r.Id, r.CompanyId, r.Keyword, r.TargetAccount, r.Direction?.ToString(), r.Priority, r.RequiresTaxMatching);
}
