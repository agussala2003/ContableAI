using ContableAI.Domain.Constants;
using ContableAI.Domain.Entities;
using ContableAI.Domain.Enums;
using ContableAI.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Globalization;

namespace ContableAI.Application.Features.JournalEntries.Commands;

public sealed class GenerateJournalEntriesCommandHandler : IRequestHandler<GenerateJournalEntriesCommand>
{
    private readonly ContableAIDbContext _dbContext;
    private readonly ILogger<GenerateJournalEntriesCommandHandler> _logger;

    public GenerateJournalEntriesCommandHandler(ContableAIDbContext dbContext, ILogger<GenerateJournalEntriesCommandHandler> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task Handle(GenerateJournalEntriesCommand request, CancellationToken cancellationToken)
    {
        if (request.TransactionIds == null || request.TransactionIds.Count == 0)
            return;

        var studioCompanyIds = await _dbContext.Companies
            .AsNoTracking()
            .Where(c => c.StudioTenantId == request.StudioTenantId && c.IsActive)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);

        var transactions = await _dbContext.BankTransactions
            .Where(t => request.TransactionIds.Contains(t.Id)
                     && t.CompanyId.HasValue
                     && studioCompanyIds.Contains(t.CompanyId.Value)
                     && t.AssignedAccount != null
                     && t.JournalEntryId == null)
            .ToListAsync(cancellationToken);

        if (transactions.Count == 0)
            return;

        var closedPeriods = await _dbContext.ClosedPeriods
            .AsNoTracking()
            .Where(p => p.StudioTenantId == request.StudioTenantId)
            .Select(p => new { p.Year, p.Month })
            .ToListAsync(cancellationToken);

        if (closedPeriods.Count > 0)
        {
            var closedSet = closedPeriods.Select(p => (p.Year, p.Month)).ToHashSet();
            var offender  = transactions.FirstOrDefault(t => closedSet.Contains((t.Date.Year, t.Date.Month)));
            if (offender != null)
            {
                _logger.LogWarning("Período cerrado detectado en Hangfire. Cancelando generación.");
                return;
            }
        }

        var companyIds = transactions.Select(t => t.CompanyId!.Value).Distinct().ToList();
        var companiesMap = await _dbContext.Companies
            .AsNoTracking()
            .Where(c => companyIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.BankAccountName, cancellationToken);

        var missingBank = companyIds
            .Where(id => !companiesMap.ContainsKey(id) || string.IsNullOrWhiteSpace(companiesMap[id]))
            .ToList();
        if (missingBank.Count > 0)
        {
            var names = await _dbContext.Companies
                .AsNoTracking()
                .Where(c => missingBank.Contains(c.Id))
                .Select(c => c.Name)
                .ToListAsync(cancellationToken);
            _logger.LogWarning(
                "Cuenta bancaria no configurada para: {Companies}. Editá la empresa y completá el campo 'Nombre de cuenta bancaria' antes de asentar.",
                string.Join(", ", names));
            return;
        }

        const int BatchSize = 500;
        var pendingEntries  = new List<JournalEntry>(BatchSize);
        var totalGenerated  = 0;
        var minTxDate = transactions.Min(t => t.Date);
        var maxTxDate = transactions.Max(t => t.Date);

        var existingEntries = await _dbContext.JournalEntries
            .AsNoTracking()
            .Include(j => j.Lines)
            .Where(j => j.CompanyId.HasValue
                     && companyIds.Contains(j.CompanyId.Value)
                     && j.Date >= minTxDate
                     && j.Date <= maxTxDate)
            .ToListAsync(cancellationToken);

        var signatureToEntryIds = existingEntries
            .GroupBy(BuildEntrySignature)
            .ToDictionary(
                g => g.Key,
                g => new Queue<Guid>(g.Select(e => e.Id)),
                StringComparer.Ordinal);

        var duplicatesSkipped = 0;

        foreach (var tx in transactions)
        {
            JournalEntry entry;
            var bankAccount = companiesMap[tx.CompanyId!.Value];
            List<JournalEntryLine> projectedLines;

            if (tx.ClassificationSource == ClassificationSources.ChequeTaxSplit)
            {
                var half1 = Math.Round(tx.Amount / 2, 2);
                var half2 = tx.Amount - half1;

                projectedLines =
                [
                    new JournalEntryLine { Account = "Impuesto a los Débitos Bancarios",  Amount = half1,     IsDebit = true  },
                    new JournalEntryLine { Account = "Impuesto a los Créditos Bancarios", Amount = half2,     IsDebit = true  },
                    new JournalEntryLine { Account = bankAccount,                          Amount = tx.Amount, IsDebit = false },
                ];

                entry = new JournalEntry
                {
                    Date              = tx.Date,
                    Description       = tx.Description,
                    CompanyId         = tx.CompanyId,
                    BankTransactionId = tx.Id,
                    Lines             = projectedLines,
                };
            }
            else
            {
                bool isDebit = tx.Type == TransactionType.Debit;

                projectedLines =
                [
                    new JournalEntryLine { Account = isDebit ? tx.AssignedAccount! : bankAccount, Amount = tx.Amount, IsDebit = true  },
                    new JournalEntryLine { Account = isDebit ? bankAccount : tx.AssignedAccount!, Amount = tx.Amount, IsDebit = false },
                ];

                entry = new JournalEntry
                {
                    Date              = tx.Date,
                    Description       = tx.Description,
                    CompanyId         = tx.CompanyId,
                    BankTransactionId = tx.Id,
                    Lines             = projectedLines,
                };
            }

            var signature = BuildEntrySignature(tx.CompanyId, tx.Date, tx.Description, projectedLines);
            if (signatureToEntryIds.TryGetValue(signature, out var existingEntryIds)
                && existingEntryIds.Count > 0)
            {
                var existingEntryId = existingEntryIds.Dequeue();
                if (existingEntryIds.Count == 0)
                    signatureToEntryIds.Remove(signature);

                tx.MarkPossibleDuplicate();
                tx.JournalEntryId = existingEntryId;
                duplicatesSkipped++;
                continue;
            }

            pendingEntries.Add(entry);
            tx.JournalEntryId = entry.Id;

            if (pendingEntries.Count >= BatchSize)
            {
                _dbContext.JournalEntries.AddRange(pendingEntries);
                await _dbContext.SaveChangesAsync(cancellationToken);
                totalGenerated += pendingEntries.Count;
                pendingEntries.Clear();
            }
        }

        if (pendingEntries.Count > 0)
            _dbContext.JournalEntries.AddRange(pendingEntries);

        totalGenerated += pendingEntries.Count;
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Background job finished: Generated {Generated}, Skipped {Skipped}", totalGenerated, duplicatesSkipped);
    }

    private static string BuildEntrySignature(JournalEntry entry)
        => BuildEntrySignature(entry.CompanyId, entry.Date, entry.Description, entry.Lines);

    private static string BuildEntrySignature(Guid? companyId, DateOnly date, string description, IEnumerable<JournalEntryLine> lines)
    {
        var normalizedDescription = NormalizeDescription(description);
        var lineSignature = string.Join(";", lines
            .OrderBy(l => l.Account, StringComparer.OrdinalIgnoreCase)
            .ThenBy(l => l.IsDebit)
            .ThenBy(l => l.Amount)
            .Select(l => string.Join("|",
                l.Account.Trim().ToUpperInvariant(),
                l.IsDebit ? "D" : "H",
                l.Amount.ToString("0.00", CultureInfo.InvariantCulture))));

        return string.Join("#",
            companyId?.ToString() ?? "NO_COMPANY",
            date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            normalizedDescription,
            lineSignature);
    }

    private static string NormalizeDescription(string description)
        => string.Join(' ', (description ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToUpperInvariant();
}
