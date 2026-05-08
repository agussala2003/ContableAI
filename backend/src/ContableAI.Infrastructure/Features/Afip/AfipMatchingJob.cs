using ContableAI.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ContableAI.Infrastructure.Features.Afip;

public class AfipMatchingJob
{
    private readonly ContableAIDbContext _db;
    private readonly ILogger<AfipMatchingJob> _logger;

    public AfipMatchingJob(ContableAIDbContext db, ILogger<AfipMatchingJob> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task RunAsync(Guid companyId)
    {
        var unmatchedVouchers = await _db.AfipVouchers
            .Where(v => v.CompanyId == companyId && !v.IsMatched)
            .ToListAsync();

        if (unmatchedVouchers.Count == 0) return;

        var pendingTxs = await _db.BankTransactions
            .Where(t => t.CompanyId == companyId && t.NeedsTaxMatching)
            .ToListAsync();

        if (pendingTxs.Count == 0) return;

        int matched = 0;
        var usedTxIds = new HashSet<Guid>();

        foreach (var voucher in unmatchedVouchers)
        {
            var tx = pendingTxs.FirstOrDefault(t =>
                !usedTxIds.Contains(t.Id) &&
                t.Amount == voucher.Amount &&
                Math.Abs(t.Date.DayNumber - voucher.Date.DayNumber) <= 2);

            if (tx == null) continue;

            tx.Assign(voucher.TaxName, null, false, "AFIP Match");
            voucher.IsMatched = true;
            voucher.MatchedTransactionId = tx.Id;
            usedTxIds.Add(tx.Id);
            matched++;
        }

        if (matched > 0)
            await _db.SaveChangesAsync();

        _logger.LogInformation("AFIP Matching job: {Matched}/{Total} vouchers matched for company {CompanyId}",
            matched, unmatchedVouchers.Count, companyId);
    }
}
