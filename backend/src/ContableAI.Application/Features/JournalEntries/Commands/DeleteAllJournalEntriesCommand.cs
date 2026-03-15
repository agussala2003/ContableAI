using ContableAI.Application.Common;
using MediatR;

namespace ContableAI.Application.Features.JournalEntries.Commands;

public record DeleteAllJournalEntriesCommand(
    string StudioTenantId,
    Guid? CompanyId,
    int? Month,
    int? Year)
    : IRequest<Result<DeleteAllJournalEntriesResponse>>;

public record DeleteAllJournalEntriesResponse(
    int DeletedEntries,
    int UnlinkedTransactions,
    string ScopeDescription);
