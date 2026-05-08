using ContableAI.Application.Common;
using MediatR;

namespace ContableAI.Application.Features.Rules.Queries;

public record GetRuleSuggestionsQuery(Guid CompanyId, string StudioTenantId)
    : IRequest<Result<List<RuleSuggestionResponse>>>;

public record RuleSuggestionResponse(
    Guid Id,
    string Keyword,
    string SuggestedAccount,
    int Frequency,
    DateTime CreatedAt
);
