using ContableAI.Application.Common;
using ContableAI.Application.Features.JournalEntries.Commands;
using ContableAI.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace ContableAI.Infrastructure.Features.JournalEntries;

public sealed class DeleteAllJournalEntriesHandler
    : IRequestHandler<DeleteAllJournalEntriesCommand, Result<DeleteAllJournalEntriesResponse>>
{
    private readonly ContableAIDbContext _db;

    public DeleteAllJournalEntriesHandler(ContableAIDbContext db)
    {
        _db = db;
    }

    public async Task<Result<DeleteAllJournalEntriesResponse>> Handle(
        DeleteAllJournalEntriesCommand request,
        CancellationToken cancellationToken)
    {
        var studioCompanyIds = await _db.Companies
            .Where(c => c.StudioTenantId == request.StudioTenantId && c.IsActive)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);

        if (studioCompanyIds.Count == 0)
            return Result<DeleteAllJournalEntriesResponse>.NotFound("No hay empresas activas para el estudio.");

        if (request.CompanyId.HasValue && !studioCompanyIds.Contains(request.CompanyId.Value))
            return Result<DeleteAllJournalEntriesResponse>.Forbidden("La empresa no pertenece al estudio actual.");

        IQueryable<Domain.Entities.JournalEntry> query = _db.JournalEntries
            .Where(j => j.CompanyId.HasValue && studioCompanyIds.Contains(j.CompanyId.Value));

        if (request.CompanyId.HasValue)
            query = query.Where(j => j.CompanyId == request.CompanyId.Value);

        if (request.Month.HasValue && request.Year.HasValue)
        {
            var start = new DateOnly(request.Year.Value, request.Month.Value, 1);
            var end = start.AddMonths(1).AddDays(-1);
            query = query.Where(j => j.Date >= start && j.Date <= end);
        }
        else if (request.Year.HasValue)
        {
            var start = new DateOnly(request.Year.Value, 1, 1);
            var end = new DateOnly(request.Year.Value, 12, 31);
            query = query.Where(j => j.Date >= start && j.Date <= end);
        }

        var entries = await query.ToListAsync(cancellationToken);
        if (entries.Count == 0)
        {
            var emptyScope = BuildScopeDescription(request.CompanyId, request.Month, request.Year);
            return Result<DeleteAllJournalEntriesResponse>.Success(
                new DeleteAllJournalEntriesResponse(0, 0, emptyScope));
        }

        var yearsMonths = entries
            .Select(e => new { e.Date.Year, e.Date.Month })
            .Distinct()
            .ToList();

        foreach (var ym in yearsMonths)
        {
            var isClosed = await _db.ClosedPeriods.AnyAsync(p =>
                p.StudioTenantId == request.StudioTenantId
                && p.Year == ym.Year
                && p.Month == ym.Month,
                cancellationToken);

            if (isClosed)
            {
                return Result<DeleteAllJournalEntriesResponse>.Failure(
                    $"El período {ym.Month:D2}/{ym.Year} está cerrado. Reabrilo antes de borrar asientos.",
                    422);
            }
        }

        var entryIds = entries.Select(e => e.Id).ToList();
        var linkedTransactions = await _db.BankTransactions
            .Where(t => t.JournalEntryId.HasValue && entryIds.Contains(t.JournalEntryId.Value))
            .ToListAsync(cancellationToken);

        foreach (var tx in linkedTransactions)
            tx.JournalEntryId = null;

        _db.JournalEntries.RemoveRange(entries);
        await _db.SaveChangesAsync(cancellationToken);

        var scope = BuildScopeDescription(request.CompanyId, request.Month, request.Year);
        return Result<DeleteAllJournalEntriesResponse>.Success(
            new DeleteAllJournalEntriesResponse(entries.Count, linkedTransactions.Count, scope));
    }

    private static string BuildScopeDescription(Guid? companyId, int? month, int? year)
    {
        var companyLabel = companyId.HasValue ? "empresa seleccionada" : "estudio";
        if (month.HasValue && year.HasValue)
            return $"{companyLabel} - {month:D2}/{year}";
        if (year.HasValue)
            return $"{companyLabel} - {year}";
        return companyLabel;
    }
}
