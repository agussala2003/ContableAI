using ContableAI.Application.Common;
using MediatR;
using ContableAI.Application.Features.Companies.Commands;

namespace ContableAI.Application.Features.Rules.Commands;

public record AcceptSuggestionResponse(RuleResponse Rule, int UpdatedTransactionCount);

public record AcceptRuleSuggestionCommand(Guid CompanyId, Guid SuggestionId, string StudioTenantId)
    : IRequest<Result<AcceptSuggestionResponse>>;

public record RejectRuleSuggestionCommand(Guid CompanyId, Guid SuggestionId)
    : IRequest<Result<bool>>;
