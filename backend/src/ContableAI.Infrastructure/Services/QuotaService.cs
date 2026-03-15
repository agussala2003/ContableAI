using ContableAI.Domain.Enums;
using ContableAI.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ContableAI.Infrastructure.Services;

/// <summary>Límites de recursos por plan.</summary>
public record QuotaLimits(
    int MaxCompanies,
    int MaxRulesPerCompany,
    int MaxMonthlyTransactions
)
{
    /// <summary>Valor de "sin límite".</summary>
    public const int Unlimited = -1;

    public static QuotaLimits ForPlan(StudioPlan plan) => plan switch
    {
        StudioPlan.Free       => new(3,   20,  200),
        StudioPlan.Pro        => new(20,  200, 2_000),
        StudioPlan.Enterprise => new(Unlimited, Unlimited, Unlimited),
        _                     => new(3,   20,  200),
    };

    public bool CompaniesOk(int current)    => MaxCompanies == Unlimited || current < MaxCompanies;
    public bool RulesOk(int current)        => MaxRulesPerCompany == Unlimited || current < MaxRulesPerCompany;
    public bool TransactionsOk(int current) => MaxMonthlyTransactions == Unlimited || current < MaxMonthlyTransactions;
}

/// <summary>Quota usage snapshot for a studio.</summary>
public record QuotaUsage(
    string  Plan,
    int     CompaniesUsed,
    int     MaxCompanies,
    int     MonthlyTransactionsUsed,
    int     MaxMonthlyTransactions,
    int     TotalRulesUsed,
    int     MaxRulesPerCompany
);

public interface IQuotaService
{
    Task<QuotaLimits>  GetLimitsAsync(string studioTenantId);
    Task<QuotaUsage>   GetUsageAsync(string studioTenantId);
    Task<bool>         CanAddCompanyAsync(string studioTenantId);
    Task<bool>         CanAddRuleAsync(string studioTenantId, Guid companyId);
    Task<bool>         CanUploadTransactionsAsync(string studioTenantId, int count);
}

public class QuotaService : IQuotaService
{
    private readonly ContableAIDbContext _db;

    public QuotaService(ContableAIDbContext db) => _db = db;

    private async Task<StudioPlan> GetPlanAsync(string studioTenantId) =>
        await _db.Users
            .Where(u => u.StudioTenantId == studioTenantId && u.Role == UserRole.StudioOwner)
            .Select(u => u.Plan)
            .FirstOrDefaultAsync();

    public async Task<QuotaLimits> GetLimitsAsync(string studioTenantId)
        => QuotaLimits.ForPlan(await GetPlanAsync(studioTenantId));

    public async Task<QuotaUsage> GetUsageAsync(string studioTenantId)
    {
        var plan   = await GetPlanAsync(studioTenantId);
        var limits = QuotaLimits.ForPlan(plan);

        var companiesUsed = await _db.Companies
            .CountAsync(c => c.StudioTenantId == studioTenantId && c.IsActive);

        var companyIds = await _db.Companies
            .Where(c => c.StudioTenantId == studioTenantId && c.IsActive)
            .Select(c => c.Id)
            .ToListAsync();

        var now = DateTime.UtcNow;
        var monthlyTx = await _db.BankTransactions
            .CountAsync(t => t.CompanyId != null
                          && companyIds.Contains(t.CompanyId.Value)
                          && t.Date.Year  == now.Year
                          && t.Date.Month == now.Month);

        var totalRules = await _db.AccountingRules
            .CountAsync(r => r.CompanyId != null && companyIds.Contains(r.CompanyId.Value));

        return new QuotaUsage(
            plan.ToString(),
            companiesUsed,
            limits.MaxCompanies,
            monthlyTx,
            limits.MaxMonthlyTransactions,
            totalRules,
            limits.MaxRulesPerCompany
        );
    }

    public async Task<bool> CanAddCompanyAsync(string studioTenantId)
    {
        var limits  = await GetLimitsAsync(studioTenantId);
        var current = await _db.Companies.CountAsync(c => c.StudioTenantId == studioTenantId && c.IsActive);
        return limits.CompaniesOk(current);
    }

    public async Task<bool> CanAddRuleAsync(string studioTenantId, Guid companyId)
    {
        var limits  = await GetLimitsAsync(studioTenantId);
        var current = await _db.AccountingRules.CountAsync(r => r.CompanyId == companyId);
        return limits.RulesOk(current);
    }

    public async Task<bool> CanUploadTransactionsAsync(string studioTenantId, int count)
    {
        var limits     = await GetLimitsAsync(studioTenantId);

        // Enterprise: unlimited — skip DB counting entirely
        if (limits.MaxMonthlyTransactions == QuotaLimits.Unlimited)
            return true;

        var now        = DateTime.UtcNow;
        var companyIds = await _db.Companies
            .Where(c => c.StudioTenantId == studioTenantId && c.IsActive)
            .Select(c => c.Id)
            .ToListAsync();
        var used = await _db.BankTransactions
            .CountAsync(t => t.CompanyId != null
                          && companyIds.Contains(t.CompanyId.Value)
                          && t.Date.Year  == now.Year
                          && t.Date.Month == now.Month);
        return limits.TransactionsOk(used + count);
    }
}
