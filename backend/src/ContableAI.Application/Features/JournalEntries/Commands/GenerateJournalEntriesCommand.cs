using ContableAI.Domain.Entities;
using MediatR;
using System.Collections.Generic;
using System;

namespace ContableAI.Application.Features.JournalEntries.Commands;

public sealed record GenerateJournalEntriesCommand(
    List<Guid> TransactionIds,
    string StudioTenantId) : IRequest;
