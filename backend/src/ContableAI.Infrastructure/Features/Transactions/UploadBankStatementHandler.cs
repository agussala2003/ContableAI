using ContableAI.Application.Common;
using ContableAI.Application.Features.Transactions.Commands;
using ContableAI.Domain.Constants;
using ContableAI.Domain.Entities;
using ContableAI.Infrastructure.Features.Afip;
using ContableAI.Infrastructure.Persistence;
using ContableAI.Infrastructure.Services;
using Hangfire;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ContableAI.Infrastructure.Features.Transactions;

public sealed class UploadBankStatementHandler
    : IRequestHandler<UploadBankStatementCommand, Result<UploadBankStatementResponse>>
{
    private static readonly HashSet<string> AllowedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".csv", ".xlsx", ".xls", ".pdf" };

    private const long MaxFileSizeBytes = 25 * 1024 * 1024;

    private readonly ContableAIDbContext    _db;
    private readonly IBankParserService     _parser;
    private readonly IClassificationService _classifier;
    private readonly ICurrentTenantService  _tenant;
    private readonly IQuotaService          _quota;
    private readonly IBackgroundJobClient   _jobs;
    private readonly ILogger<UploadBankStatementHandler> _logger;

    public UploadBankStatementHandler(
        ContableAIDbContext    db,
        IBankParserService     parser,
        IClassificationService classifier,
        ICurrentTenantService  tenant,
        IQuotaService          quota,
        IBackgroundJobClient   jobs,
        ILogger<UploadBankStatementHandler> logger)
    {
        _db         = db;
        _parser     = parser;
        _classifier = classifier;
        _tenant     = tenant;
        _quota      = quota;
        _jobs       = jobs;
        _logger     = logger;
    }

    public async Task<Result<UploadBankStatementResponse>> Handle(
        UploadBankStatementCommand command,
        CancellationToken          ct)
    {
        // ── File validation ────────────────────────────────────────────────────
        foreach (var file in command.Files)
        {
            if (file.Length > MaxFileSizeBytes)
                return Result<UploadBankStatementResponse>.Failure(
                    $"El archivo '{file.FileName}' supera el máximo permitido de 25 MB.");

            var ext = Path.GetExtension(file.FileName ?? string.Empty);
            if (!AllowedExtensions.Contains(ext))
                return Result<UploadBankStatementResponse>.Failure(
                    $"Formato no soportado para '{file.FileName}'. Solo se permiten CSV, XLSX y PDF.");
        }

        // ── Company resolution & tenant check ─────────────────────────────────
        Company? company = null;
        if (command.CompanyId.HasValue)
            company = await _db.Companies.FindAsync([command.CompanyId.Value], ct);

        if (company != null && company.StudioTenantId != _tenant.StudioTenantId)
            return Result<UploadBankStatementResponse>.Forbidden();

        // ── Parse all files ────────────────────────────────────────────────────
        var allParsed = new List<(FileData File, List<BankTransaction> Txs)>();
        foreach (var file in command.Files)
        {
            if (file.Length == 0) continue;
            using var stream = new MemoryStream(file.Content);
            var effectiveBankCode = (command.BankCode is null or "AUTO") ? "PDF" : command.BankCode;
            List<BankTransaction> txs;
            try
            {
                txs = _parser.Parse(stream, effectiveBankCode, file.FileName ?? "upload.pdf").ToList();
            }
            catch (InvalidOperationException ex)
            {
                return Result<UploadBankStatementResponse>.Failure(
                    $"No se pudo procesar '{file.FileName}': {ex.Message}");
            }
            allParsed.Add((file, txs));
        }

        var totalParsed = allParsed.Sum(x => x.Txs.Count);

        // ── Quota check (only current-month transactions count) ────────────────
        var tenantIdForQuota = _tenant.StudioTenantId;
        if (!string.IsNullOrEmpty(tenantIdForQuota) && totalParsed > 0)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var currentMonthCount = allParsed.Sum(x =>
                x.Txs.Count(t => t.Date.Year == today.Year && t.Date.Month == today.Month));

            if (currentMonthCount > 0 &&
                !await _quota.CanUploadTransactionsAsync(tenantIdForQuota, currentMonthCount))
                return Result<UploadBankStatementResponse>.PaymentRequired(
                    "QUOTA_EXCEEDED",
                    "Alcanzaste el límite mensual de transacciones de tu plan. Actualizá a Pro para continuar.");
        }

        var tenantFilter = company?.Id.ToString() ?? "ESTUDIO_DEFAULT";

        // ── Date range of the parsed batch ────────────────────────────────────
        DateOnly? minParsedDate = null;
        DateOnly? maxParsedDate = null;
        if (totalParsed > 0)
        {
            var parsedDates = allParsed.SelectMany(x => x.Txs).Select(t => t.Date);
            minParsedDate = parsedDates.Min();
            maxParsedDate = parsedDates.Max();
        }

        // ── Existing signatures (O(1) lookup dictionary) ───────
        var existingSignaturesQuery = _db.BankTransactions.AsQueryable(); // Tracking if we want to mutate
        existingSignaturesQuery = company != null
            ? existingSignaturesQuery.Where(t => t.CompanyId == company.Id)
            : existingSignaturesQuery.Where(t => t.TenantId == "ESTUDIO_DEFAULT");

        if (minParsedDate.HasValue && maxParsedDate.HasValue)
            existingSignaturesQuery = existingSignaturesQuery
                .Where(t => t.Date >= minParsedDate.Value && t.Date <= maxParsedDate.Value);

        var existingSignaturesList = await existingSignaturesQuery.ToListAsync(ct);

        var existingSignatures = new Dictionary<string, BankTransaction>(StringComparer.Ordinal);
        
        foreach (var s in existingSignaturesList)
        {
            var sig = s.ExternalId != null
                ? $"EXT|{s.ExternalId}|{s.Date}|{s.Amount}|{s.Description}|{s.Type}"
                : $"{s.Date}|{s.Description}|{s.Amount}|{s.Type}";
            existingSignatures[sig] = s;
        }

        // ── Classification rules (loaded once for the whole batch) ─────────────
        var companyRules = company != null
            ? await _db.AccountingRules.AsNoTracking()
                .Where(r => r.CompanyId == company.Id)
                .OrderBy(r => r.Priority)
                .ToListAsync(ct)
            : new List<AccountingRule>();

        Guid? studioGuid = company != null && Guid.TryParse(company.StudioTenantId, out var sg) ? sg : null;

        var studioRules = studioGuid.HasValue
            ? await _db.AccountingRules.AsNoTracking()
                .Where(r => r.CompanyId == null && r.StudioTenantId == studioGuid)
                .OrderBy(r => r.Priority)
                .ToListAsync(ct)
            : new List<AccountingRule>();

        var systemRules = await _db.AccountingRules.AsNoTracking()
            .Where(r => r.CompanyId == null && r.StudioTenantId == null)
            .OrderBy(r => r.Priority)
            .ToListAsync(ct);

        var allRules = companyRules.Concat(studioRules).Concat(systemRules).ToList();

        // ── Union-duplicate candidates (single query for the batch date range) ──
        var existingUnionCandidates = new List<(DateOnly Date, string Description, decimal Amount)>();
        if (company != null && minParsedDate.HasValue && maxParsedDate.HasValue)
        {
            var unionMin = minParsedDate.Value.AddDays(-3);
            var unionMax = maxParsedDate.Value.AddDays(3);

            var raw = await _db.BankTransactions.AsNoTracking()
                .Where(t => t.CompanyId == company.Id && t.Date >= unionMin && t.Date <= unionMax)
                .Select(t => new { t.Date, t.Description, t.Amount })
                .ToListAsync(ct);

            existingUnionCandidates = raw.Select(x => (x.Date, x.Description, x.Amount)).ToList();
        }

        // ── Process each file ──────────────────────────────────────────────────
        var allClassified        = new List<BankTransaction>();
        int totalDuplicates      = 0;
        var perFileResults       = new List<FileUploadResult>();
        var acceptedInBatch      = new HashSet<string>(StringComparer.Ordinal);
        int globalSortOrder      = 0;
        int totalReapplied       = 0;
        var allSkippedDuplicates = new List<SkippedDuplicateItem>();

        foreach (var (file, parsedTransactions) in allParsed)
        {
            var classified = new List<BankTransaction>();
            int fileDups   = 0;

            foreach (var tx in parsedTransactions)
            {
                var sig = tx.ExternalId != null
                    ? $"EXT|{tx.ExternalId}|{tx.Date}|{tx.Amount}|{tx.Description}|{tx.Type}"
                    : $"{tx.Date}|{tx.Description}|{tx.Amount}|{tx.Type}";

                if (existingSignatures.TryGetValue(sig, out var existingTx))
                {
                    if (command.ForceReapplyRules && existingTx.JournalEntryId == null)
                    {
                        var reclassified = await _classifier.ClassifyAsync(tx, allRules, company?.SplitChequeTax ?? false, ct);
                        if (existingTx.AssignedAccount != reclassified.AssignedAccount || existingTx.ClassificationSource != reclassified.ClassificationSource)
                        {
                            existingTx.Assign(
                                reclassified.AssignedAccount ?? "Pending",
                                reclassified.AppliedRuleId,
                                reclassified.NeedsTaxMatching,
                                reclassified.ClassificationSource,
                                reclassified.ConfidenceScore
                            );
                            totalReapplied++;
                        }
                    }
                    else
                    {
                        fileDups++;
                        totalDuplicates++;
                        allSkippedDuplicates.Add(new SkippedDuplicateItem(tx.Date, tx.Amount, tx.Description));
                    }
                    continue;
                }

                if (!acceptedInBatch.Add(sig))
                {
                    fileDups++;
                    totalDuplicates++;
                    allSkippedDuplicates.Add(new SkippedDuplicateItem(tx.Date, tx.Amount, tx.Description));
                    continue;
                }

                tx.TenantId  = tenantFilter;
                tx.CompanyId = company?.Id;
                tx.SortOrder = globalSortOrder++;

                var result = await _classifier.ClassifyAsync(tx, allRules, company?.SplitChequeTax ?? false, ct);
                classified.Add(result);
            }

            // Union-duplicate detection within this file
            if (classified.Any())
            {
                var unionBatch = classified
                    .Where(t => UnionKeywords.All.Any(k =>
                        t.Description.Contains(k, StringComparison.OrdinalIgnoreCase)))
                    .ToList();

                if (unionBatch.Any() && company != null)
                {
                    var currentBatchView = allClassified.Concat(classified).ToList();
                    foreach (var tx in unionBatch)
                    {
                        var kw = UnionKeywords.All.FirstOrDefault(k =>
                            tx.Description.Contains(k, StringComparison.OrdinalIgnoreCase));
                        if (kw == null) continue;

                        bool dup = existingUnionCandidates.Any(e =>
                            e.Amount == tx.Amount &&
                            Math.Abs(e.Date.DayNumber - tx.Date.DayNumber) <= 2 &&
                            e.Description.Contains(kw, StringComparison.OrdinalIgnoreCase));

                        if (!dup)
                            dup = currentBatchView
                                .Where(o => o != tx)
                                .Any(o => o.Amount == tx.Amount &&
                                          Math.Abs(o.Date.DayNumber - tx.Date.DayNumber) <= 2 &&
                                          o.Description.Contains(kw, StringComparison.OrdinalIgnoreCase));

                        if (dup) tx.MarkPossibleDuplicate();
                    }
                }
            }

            allClassified.AddRange(classified);
            perFileResults.Add(new FileUploadResult(file.FileName, classified.Count, fileDups));
        }

        if (allClassified.Any() || totalReapplied > 0)
        {
            if (allClassified.Any())
                await _db.BankTransactions.AddRangeAsync(allClassified, ct);
            await _db.SaveChangesAsync(ct);
        }

        // Auto-trigger AFIP matching si la empresa tiene vouchers pendientes de cruce
        if (company != null && allClassified.Any(t => t.NeedsTaxMatching))
            _jobs.Enqueue<AfipMatchingJob>(job => job.RunAsync(company.Id));

        _logger.LogInformation(
            "Upload completed: {FileCount} file(s), {TotalParsed} parsed, {Duplicates} duplicates, {Reapplied} reapplied",
            command.Files.Count, allClassified.Count + totalDuplicates, totalDuplicates, totalReapplied);

        return Result<UploadBankStatementResponse>.Success(new UploadBankStatementResponse(
            TotalFiles:          command.Files.Count,
            TotalProcessed:      allClassified.Count,
            DuplicatesSkipped:   totalDuplicates,
            ReappliedToExisting: totalReapplied,
            CompanyName:         company?.Name ?? "Sin empresa",
            PerFile:             perFileResults,
            SkippedDuplicates:   allSkippedDuplicates
        ));
    }
}
