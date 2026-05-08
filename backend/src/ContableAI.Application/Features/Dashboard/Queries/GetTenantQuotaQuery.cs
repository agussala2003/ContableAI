using ContableAI.Application.Common;
using MediatR;

namespace ContableAI.Application.Features.Dashboard.Queries;

public record GetTenantQuotaQuery(string StudioTenantId) : IRequest<Result<TenantQuotaResponse>>;

public record TenantQuotaResponse(
    string Plan,
    int CompaniesUsed,
    int MaxCompanies,
    int MonthlyTransactionsUsed,
    int MaxMonthlyTransactions,
    int TotalRulesUsed,
    int MaxRules
);
