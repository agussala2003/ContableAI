using ContableAI.Application.Common;
using ContableAI.Application.Features.Dashboard.Queries;
using ContableAI.Domain.Constants;
using ContableAI.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ContableAI.Infrastructure.Services;

namespace ContableAI.Infrastructure.Features.Dashboard;

public sealed class GetDashboardStatsHandler
    : IRequestHandler<GetDashboardStatsQuery, Result<DashboardStatsResponse>>
{
    private readonly ContableAIDbContext _db;

    public GetDashboardStatsHandler(ContableAIDbContext db) => _db = db;

    public async Task<Result<DashboardStatsResponse>> Handle(
        GetDashboardStatsQuery query,
        CancellationToken      ct)
    {
        var now   = DateTime.UtcNow;
        var month = query.Month ?? now.Month;
        var year  = query.Year  ?? now.Year;

        // Single-pass: project only the two columns needed, then aggregate in memory.
        // EF Core translates this to: SELECT "ClassificationSource", "ConfidenceScore"
        // WHERE CompanyId = @p AND EXTRACT(month FROM Date) = @m AND EXTRACT(year FROM Date) = @y
        var rows = _db.BankTransactions
            .AsNoTracking()
            .Where(t =>
                t.CompanyId == query.CompanyId &&
                t.Date.Month == month          &&
                t.Date.Year  == year);

        // Filtro por banco (Fase C): se resuelve por las cuentas de la empresa que pertenecen a ese
        // banco, sin desnormalizar el código en cada movimiento. Una empresa tiene un puñado de
        // cuentas, así que la lista se materializa y el predicado queda como un IN sobre
        // BankAccountId — la columna que ya indexa IX_BankTransactions_BankAccountId_Date.
        if (query.NoBankOnly || query.BankCode is not null)
        {
            var accounts = await _db.BankAccounts
                .AsNoTracking()
                .Where(a => a.CompanyId == query.CompanyId)
                .Select(a => new { a.Id, a.BankCode })
                .ToListAsync(ct);

            var ids = accounts
                .Where(a => string.Equals(a.BankCode, query.NoBankOnly ? null : query.BankCode, StringComparison.OrdinalIgnoreCase))
                .Select(a => a.Id)
                .ToList();

            rows = query.NoBankOnly
                ? rows.Where(t => !t.BankAccountId.HasValue || ids.Contains(t.BankAccountId.Value))
                : rows.Where(t => t.BankAccountId.HasValue && ids.Contains(t.BankAccountId.Value));
        }

        var projected = await rows
            .Select(t => new
            {
                t.ClassificationSource,
                t.ConfidenceScore,
            })
            .ToListAsync(ct);

        var total   = projected.Count;
        var pending = projected.Count(r => r.ClassificationSource == ClassificationSources.Pending);
        var classified    = total - pending;
        var lowConfidence = projected.Count(r =>
            r.ClassificationSource != ClassificationSources.Pending &&
            r.ConfidenceScore < 0.5f);

        return Result<DashboardStatsResponse>.Success(new DashboardStatsResponse(
            TotalTransactions:    total,
            PendingClassification: pending,
            Classified:           classified,
            LowConfidence:        lowConfidence,
            Month:                month,
            Year:                 year
        ));
    }
}

public sealed class GetTenantQuotaHandler
    : IRequestHandler<GetTenantQuotaQuery, Result<TenantQuotaResponse>>
{
    private readonly IQuotaService _quota;

    public GetTenantQuotaHandler(IQuotaService quota) => _quota = quota;

    public async Task<Result<TenantQuotaResponse>> Handle(
        GetTenantQuotaQuery query,
        CancellationToken   ct)
    {
        var usage = await _quota.GetUsageAsync(query.StudioTenantId);

        var response = new TenantQuotaResponse(
            Plan:                    usage.Plan,
            CompaniesUsed:           usage.CompaniesUsed,
            MaxCompanies:            usage.MaxCompanies,
            MonthlyTransactionsUsed: usage.MonthlyTransactionsUsed,
            MaxMonthlyTransactions:  usage.MaxMonthlyTransactions,
            TotalRulesUsed:          usage.TotalRulesUsed,
            MaxRules:                usage.MaxRulesPerCompany
        );

        return Result<TenantQuotaResponse>.Success(response);
    }
}
