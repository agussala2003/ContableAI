using ContableAI.Application.Common;
using MediatR;

namespace ContableAI.Application.Features.Admin.Queries;

public record AdminPlanCountResponse(string Plan, int Count);

public record AdminUserRowResponse(
    Guid Id,
    string Email,
    string DisplayName,
    string Role,
    int AccountStatus,
    string StudioTenantId,
    DateTime CreatedAt,
    string Plan,
    int CompaniesCount,
    int MaxCompanies,
    int MonthlyTxUsed,
    int MaxMonthlyTransactions
);

public record AdminStatsResponse(
    int TotalUsers,
    int ActiveUsers,
    int PendingUsers,
    int SuspendedUsers,
    int TotalCompanies,
    int TotalTransactions,
    int MonthlyTransactions,
    int TotalJournalEntries,
    List<AdminPlanCountResponse> PlanDistribution
);

public record AdminGlobalRuleResponse(
    Guid Id,
    string Keyword,
    string TargetAccount,
    string? Direction,
    int Priority,
    bool RequiresTaxMatching
);

public record GetAdminUsersQuery() : IRequest<Result<List<AdminUserRowResponse>>>;

public record GetAdminStatsQuery() : IRequest<Result<AdminStatsResponse>>;

public record GetAdminGlobalRulesQuery() : IRequest<Result<List<AdminGlobalRuleResponse>>>;
