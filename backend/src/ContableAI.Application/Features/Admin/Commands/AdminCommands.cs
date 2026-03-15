using ContableAI.Application.Common;
using ContableAI.Application.Features.Admin.Queries;
using MediatR;

namespace ContableAI.Application.Features.Admin.Commands;

public record AdminMessageResponse(string Message);

public record AdminDbResetResponse(
    string Message,
    string NextStep,
    string SeedAdminUrl,
    int GlobalRulesSeeded,
    int AccountsSeeded
);

public record ActivateUserCommand(Guid Id) : IRequest<Result<AdminMessageResponse>>;

public record SuspendUserCommand(Guid Id) : IRequest<Result<AdminMessageResponse>>;

public record AdminUserPlanResponse(Guid Id, string Email, string Plan);

public record UpdateUserPlanCommand(Guid Id, string Plan) : IRequest<Result<AdminUserPlanResponse>>;

public record AdminUserRoleResponse(Guid Id, string Email, string Role);

public record UpdateUserRoleCommand(Guid Id, string Role) : IRequest<Result<AdminUserRoleResponse>>;

public record AdminUserDisplayNameResponse(Guid Id, string Email, string DisplayName);

public record UpdateUserDisplayNameCommand(Guid Id, string DisplayName) : IRequest<Result<AdminUserDisplayNameResponse>>;

public record DeleteUserCommand(Guid Id) : IRequest<Result<AdminMessageResponse>>;

public record SendAdminPasswordResetCommand(Guid Id) : IRequest<Result<AdminMessageResponse>>;

public record CreateAdminGlobalRuleCommand(
    string Keyword,
    string TargetAccount,
    string? Direction,
    int? Priority,
    bool? RequiresTaxMatching
) : IRequest<Result<AdminGlobalRuleResponse>>;

public record UpdateAdminGlobalRuleCommand(
    Guid Id,
    string Keyword,
    string TargetAccount,
    string? Direction,
    int? Priority,
    bool? RequiresTaxMatching
) : IRequest<Result<AdminGlobalRuleResponse>>;

public record DeleteAdminGlobalRuleCommand(Guid Id) : IRequest<Result<AdminMessageResponse>>;

public record AdminDbResetCommand() : IRequest<Result<AdminDbResetResponse>>;
