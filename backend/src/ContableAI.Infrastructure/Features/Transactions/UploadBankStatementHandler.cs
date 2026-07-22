using ContableAI.Application.Common;
using ContableAI.Application.Features.Transactions.Commands;
using ContableAI.Domain.Common;
using ContableAI.Domain.Constants;
using ContableAI.Domain.Entities;
using ContableAI.Infrastructure.Features.Afip;
using ContableAI.Infrastructure.Persistence;
using ContableAI.Infrastructure.Services;
using Hangfire;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace ContableAI.Infrastructure.Features.Transactions;

public sealed class UploadBankStatementHandler
    : IRequestHandler<UploadBankStatementCommand, Result<UploadBankStatementResponse>>
{
    public static readonly HashSet<string> AllowedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".csv", ".xlsx", ".xls", ".pdf" };

    public const long MaxFileSizeBytes = 25 * 1024 * 1024;

    // ── Firmas binarias (magic bytes) esperadas por extensión (M-2) ──────────────
    private static readonly byte[] SigPdf  = "%PDF-"u8.ToArray();
    private static readonly byte[] SigZip  = [0x50, 0x4B, 0x03, 0x04];               // .xlsx (OOXML = ZIP)
    private static readonly byte[] SigOle2 = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]; // .xls (OLE2 legacy)

    /// <summary>
    /// Verifica que el contenido empiece con la firma binaria correspondiente a su extensión.
    /// CSV es texto plano sin firma → se acepta. Cualquier otra extensión → inválida.
    /// Llamada desde el endpoint (validación rápida y síncrona, antes de encolar el job).
    /// </summary>
    public static bool HasValidSignature(string ext, byte[] content)
    {
        var span = content.AsSpan();
        return ext.ToLowerInvariant() switch
        {
            ".csv"  => true,
            ".pdf"  => span.StartsWith(SigPdf),
            ".xlsx" => span.StartsWith(SigZip),
            ".xls"  => span.StartsWith(SigOle2),
            _       => false,
        };
    }

    private readonly ContableAIDbContext    _db;
    private readonly IBankParserService     _parser;
    private readonly IClassificationService _classifier;
    private readonly IQuotaService          _quota;
    private readonly IBackgroundJobClient   _jobs;
    private readonly ILogger<UploadBankStatementHandler> _logger;

    public UploadBankStatementHandler(
        ContableAIDbContext    db,
        IBankParserService     parser,
        IClassificationService classifier,
        IQuotaService          quota,
        IBackgroundJobClient   jobs,
        ILogger<UploadBankStatementHandler> logger)
    {
        _db         = db;
        _parser     = parser;
        _classifier = classifier;
        _quota      = quota;
        _jobs       = jobs;
        _logger     = logger;
    }

    /// <summary>
    /// Punto de entrada del job de Hangfire (encolado como <c>sender.Send(command)</c>, sin clase
    /// de job dedicada — mismo patrón que <c>GenerateJournalEntriesCommandHandler</c>). Garantiza
    /// dos cosas para cualquier resultado (éxito, rechazo de negocio, o excepción inesperada):
    /// 1) queda una fila en <c>UploadJobResults</c> para que el endpoint de polling la lea, y
    /// 2) el staging de <c>StagedUploadFiles</c> consumido se borra. Nunca relanza: si el trabajo
    /// tirara una excepción, Hangfire reintentaría el job automáticamente y reprocesaría el mismo
    /// archivo — indeseable, porque ya habríamos grabado un resultado para el polling.
    /// </summary>
    public async Task<Result<UploadBankStatementResponse>> Handle(
        UploadBankStatementCommand command,
        CancellationToken          ct)
    {
        Result<UploadBankStatementResponse> result;
        try
        {
            result = await ProcessAsync(command, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Upload job {UploadId} falló inesperadamente", command.UploadId);
            result = Result<UploadBankStatementResponse>.Failure(
                "Error inesperado al procesar el extracto.", 500);
        }

        await SaveJobResultAsync(command, result, ct);
        return result;
    }

    /// <summary>
    /// Orquesta la subida de extractos: carga el contenido en staging → valida → resuelve empresa →
    /// parsea → chequea cuota → carga el presupuesto de duplicados, reglas y candidatos de unión →
    /// clasifica y deduplica → persiste. Cada paso vive en un método propio para que este flujo se
    /// lea de arriba a abajo.
    /// </summary>
    private async Task<Result<UploadBankStatementResponse>> ProcessAsync(
        UploadBankStatementCommand command, CancellationToken ct)
    {
        var stagedFiles = await LoadStagedFilesAsync(command.Files, ct);
        try
        {
            var (company, forbidden) = await ResolveCompanyAsync(command, ct);
            if (forbidden)
                return Result<UploadBankStatementResponse>.Forbidden();

            var (allParsed, parseErrors) = ParseFiles(command, stagedFiles);
            if (allParsed.Count == 0 && parseErrors.Count > 0)
                return Result<UploadBankStatementResponse>.Failure(
                    $"No se pudo procesar ningún archivo. {parseErrors[0]}");

            if (await QuotaExceededAsync(command, allParsed, ct))
                return Result<UploadBankStatementResponse>.PaymentRequired(
                    "QUOTA_EXCEEDED",
                    "Alcanzaste el límite mensual de transacciones de tu plan. Actualizá a Pro para continuar.");

            var (minDate, maxDate) = DateRange(allParsed);
            var budget          = await BuildDedupBudgetAsync(company, minDate, maxDate, ct);
            var rules           = await LoadRulesAsync(company, ct);
            var unionCandidates = await LoadUnionCandidatesAsync(company, minDate, maxDate, ct);

            var outcome = await ClassifyFilesAsync(command, company, allParsed, budget, rules, unionCandidates, ct);

            await PersistAsync(outcome, ct);
            EnqueueAfipMatchingIfNeeded(company, outcome);

            _logger.LogInformation(
                "Upload completed: {FileCount} file(s), {TotalParsed} parsed, {Duplicates} duplicates, {Reapplied} reapplied",
                stagedFiles.Count, outcome.Classified.Count + outcome.TotalDuplicates, outcome.TotalDuplicates, outcome.TotalReapplied);

            return BuildResponse(stagedFiles.Count, company, outcome, parseErrors);
        }
        finally
        {
            // Staging consumido: se borra pase lo que pase (éxito, rechazo o excepción), en la
            // misma transacción que el resto de los cambios de este job (RemoveRange + SaveChanges,
            // no ExecuteDeleteAsync — el proveedor InMemory usado en tests no soporta bulk delete).
            if (stagedFiles.Count > 0)
                _db.StagedUploadFiles.RemoveRange(stagedFiles.Select(f => f.Entity));
        }
    }

    // ── Pasos del pipeline ──────────────────────────────────────────────────────

    /// <summary>Contenido ya leído desde <c>StagedUploadFiles</c>, listo para parsear.</summary>
    private sealed record LoadedFile(StagedUploadFile Entity, string FileName, byte[] Content);

    private async Task<List<LoadedFile>> LoadStagedFilesAsync(
        IReadOnlyList<StagedFileRef> files, CancellationToken ct)
    {
        var ids = files.Select(f => f.StagedFileId).ToList();
        var staged = await _db.StagedUploadFiles
            .Where(f => ids.Contains(f.Id))
            .ToDictionaryAsync(f => f.Id, ct);

        return files
            .Where(f => staged.ContainsKey(f.StagedFileId))
            .Select(f => new LoadedFile(staged[f.StagedFileId], f.FileName, staged[f.StagedFileId].Content))
            .ToList();
    }

    /// <summary>Resuelve la empresa del comando y verifica que pertenezca al tenant del job.</summary>
    private async Task<(Company? company, bool forbidden)> ResolveCompanyAsync(
        UploadBankStatementCommand command, CancellationToken ct)
    {
        Company? company = null;
        if (command.CompanyId.HasValue)
            company = await _db.Companies.FindAsync([command.CompanyId.Value], ct);

        if (company != null && company.StudioTenantId != command.StudioTenantId)
            return (company, true);

        return (company, false);
    }

    /// <summary>Parsea todos los archivos. Un archivo roto no aborta el lote: se acumula su error.</summary>
    private (List<(LoadedFile File, List<BankTransaction> Txs)> parsed, List<string> errors) ParseFiles(
        UploadBankStatementCommand command, List<LoadedFile> stagedFiles)
    {
        var allParsed   = new List<(LoadedFile File, List<BankTransaction> Txs)>();
        var parseErrors = new List<string>();

        foreach (var file in stagedFiles)
        {
            if (file.Content.Length == 0) continue;
            using var stream = new MemoryStream(file.Content);
            var effectiveBankCode = (command.BankCode is null or "AUTO") ? "PDF" : command.BankCode;
            try
            {
                var txs = _parser.Parse(stream, effectiveBankCode, file.FileName ?? "upload.pdf").ToList();
                allParsed.Add((file, txs));
            }
            catch (Exception ex)
            {
                var msg = ex is InvalidOperationException ? ex.Message : $"Error inesperado: {ex.Message}";
                _logger.LogWarning("Could not parse '{FileName}': {Message}", file.FileName, msg);
                parseErrors.Add($"{file.FileName}: {msg}");
            }
        }

        return (allParsed, parseErrors);
    }

    /// <summary>Chequea la cuota mensual del plan (solo cuentan los movimientos del mes en curso).</summary>
    private async Task<bool> QuotaExceededAsync(
        UploadBankStatementCommand command,
        List<(LoadedFile File, List<BankTransaction> Txs)> allParsed, CancellationToken ct)
    {
        var tenantId    = command.StudioTenantId;
        var totalParsed = allParsed.Sum(x => x.Txs.Count);
        if (string.IsNullOrEmpty(tenantId) || totalParsed == 0)
            return false;

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var currentMonthCount = allParsed.Sum(x =>
            x.Txs.Count(t => t.Date.Year == today.Year && t.Date.Month == today.Month));

        return currentMonthCount > 0 &&
               !await _quota.CanUploadTransactionsAsync(tenantId, currentMonthCount);
    }

    /// <summary>Rango de fechas del lote parseado (null/null si no hubo movimientos).</summary>
    private static (DateOnly? min, DateOnly? max) DateRange(
        List<(LoadedFile File, List<BankTransaction> Txs)> allParsed)
    {
        var dates = allParsed.SelectMany(x => x.Txs).Select(t => t.Date).ToList();
        return dates.Count == 0 ? (null, null) : (dates.Min(), dates.Max());
    }

    /// <summary>
    /// Presupuesto de deduplicación: cuántas copias de cada firma ya existen en la BD (para el rango
    /// del lote) y una referencia a la primera de cada firma (para reaplicar reglas si corresponde).
    /// Permite N movimientos idénticos legítimos si la BD ya tiene N copias de esa firma.
    /// </summary>
    private async Task<DedupBudget> BuildDedupBudgetAsync(
        Company? company, DateOnly? minDate, DateOnly? maxDate, CancellationToken ct)
    {
        // IgnoreQueryFilters: sin HttpContext (job de Hangfire) el filtro global ya viene
        // desactivado, pero se mantiene explícito por claridad — acota por CompanyId igual.
        var query = _db.BankTransactions.IgnoreQueryFilters().AsQueryable();
        query = company != null
            ? query.Where(t => t.CompanyId == company.Id)
            : query.Where(t => t.TenantId == "ESTUDIO_DEFAULT");

        if (minDate.HasValue && maxDate.HasValue)
            query = query.Where(t => t.Date >= minDate.Value && t.Date <= maxDate.Value);

        var existing = await query.ToListAsync(ct);

        var remaining  = new Dictionary<string, int>(StringComparer.Ordinal);
        var firstMatch = new Dictionary<string, BankTransaction>(StringComparer.Ordinal);
        foreach (var s in existing)
        {
            var sig = TransactionSignatureBuilder.Build(s);
            remaining[sig] = remaining.GetValueOrDefault(sig, 0) + 1;
            firstMatch.TryAdd(sig, s);
        }

        return new DedupBudget(remaining, firstMatch);
    }

    /// <summary>Carga las reglas de clasificación aplicables (empresa + estudio + sistema), por prioridad.</summary>
    private async Task<List<AccountingRule>> LoadRulesAsync(Company? company, CancellationToken ct)
    {
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

        return companyRules.Concat(studioRules).Concat(systemRules).ToList();
    }

    /// <summary>Candidatos para la detección de duplicados "de unión" (una sola query por el rango ±3 días).</summary>
    private async Task<List<(DateOnly Date, string Description, decimal Amount, string Currency)>> LoadUnionCandidatesAsync(
        Company? company, DateOnly? minDate, DateOnly? maxDate, CancellationToken ct)
    {
        if (company == null || !minDate.HasValue || !maxDate.HasValue)
            return [];

        var unionMin = minDate.Value.AddDays(-3);
        var unionMax = maxDate.Value.AddDays(3);

        var raw = await _db.BankTransactions.AsNoTracking()
            .Where(t => t.CompanyId == company.Id && t.Date >= unionMin && t.Date <= unionMax)
            .Select(t => new { t.Date, t.Description, t.Amount, t.Currency })
            .ToListAsync(ct);

        return raw.Select(x => (x.Date, x.Description, x.Amount, x.Currency)).ToList();
    }

    /// <summary>
    /// Clasifica cada movimiento parseado, descontándolo del presupuesto de duplicados. Si
    /// <c>ForceReapplyRules</c> está activo, reclasifica el movimiento existente (sin asiento) en
    /// vez de saltarlo. Finalmente marca posibles duplicados "de unión" dentro del propio lote.
    /// </summary>
    private async Task<ClassificationOutcome> ClassifyFilesAsync(
        UploadBankStatementCommand command,
        Company? company,
        List<(LoadedFile File, List<BankTransaction> Txs)> allParsed,
        DedupBudget budget,
        List<AccountingRule> allRules,
        List<(DateOnly Date, string Description, decimal Amount, string Currency)> existingUnionCandidates,
        CancellationToken ct)
    {
        var tenantFilter = company?.Id.ToString() ?? "ESTUDIO_DEFAULT";

        var allClassified        = new List<BankTransaction>();
        int totalDuplicates      = 0;
        var perFileResults       = new List<FileUploadResult>();
        int globalSortOrder      = 0;
        int totalReapplied       = 0;
        var allSkippedDuplicates = new List<SkippedDuplicateItem>();

        foreach (var (file, parsedTransactions) in allParsed)
        {
            var classified = new List<BankTransaction>();
            int fileDups   = 0;

            foreach (var tx in parsedTransactions)
            {
                // La moneda forma parte de la firma: dos movimientos de igual importe/fecha en
                // distinta moneda (ej. 639,40 ARS vs 639,40 USD) no son duplicados.
                var sig = TransactionSignatureBuilder.Build(tx);

                // Consume one DB "slot" for this signature if available; only then it's a duplicate.
                // This allows N identical transactions if the DB has fewer than N copies.
                if (budget.Remaining.GetValueOrDefault(sig, 0) > 0)
                {
                    budget.Remaining[sig]--;

                    if (command.ForceReapplyRules &&
                        budget.FirstMatch.TryGetValue(sig, out var existingTx) &&
                        existingTx.JournalEntryId == null)
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
                        allSkippedDuplicates.Add(new SkippedDuplicateItem(tx.Date, tx.Amount, tx.Description, tx.Currency));
                    }
                    continue;
                }

                tx.TenantId       = tenantFilter;
                tx.CompanyId      = company?.Id;
                // P-2: estampa el estudio desnormalizado que usa el Global Query Filter.
                tx.StudioTenantId = company?.StudioTenantId ?? command.StudioTenantId;
                tx.SortOrder      = globalSortOrder++;

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
                            e.Currency == tx.Currency &&
                            Math.Abs(e.Date.DayNumber - tx.Date.DayNumber) <= 2 &&
                            e.Description.Contains(kw, StringComparison.OrdinalIgnoreCase));

                        if (!dup)
                            dup = currentBatchView
                                .Where(o => o != tx)
                                .Any(o => o.Amount == tx.Amount &&
                                          o.Currency == tx.Currency &&
                                          Math.Abs(o.Date.DayNumber - tx.Date.DayNumber) <= 2 &&
                                          o.Description.Contains(kw, StringComparison.OrdinalIgnoreCase));

                        if (dup) tx.MarkPossibleDuplicate();
                    }
                }
            }

            allClassified.AddRange(classified);
            perFileResults.Add(new FileUploadResult(file.FileName, classified.Count, fileDups));
        }

        return new ClassificationOutcome(
            allClassified, perFileResults, allSkippedDuplicates, totalDuplicates, totalReapplied);
    }

    /// <summary>Persiste los nuevos movimientos y los cambios de reclasificación en una sola transacción.</summary>
    private async Task PersistAsync(ClassificationOutcome outcome, CancellationToken ct)
    {
        if (outcome.Classified.Any() || outcome.TotalReapplied > 0)
        {
            if (outcome.Classified.Any())
                await _db.BankTransactions.AddRangeAsync(outcome.Classified, ct);
            await _db.SaveChangesAsync(ct);
        }
    }

    /// <summary>Dispara el cruce AFIP en segundo plano si la empresa tiene movimientos que lo requieren.</summary>
    private void EnqueueAfipMatchingIfNeeded(Company? company, ClassificationOutcome outcome)
    {
        if (company != null && outcome.Classified.Any(t => t.NeedsTaxMatching))
            _jobs.Enqueue<AfipMatchingJob>(job => job.RunAsync(company.Id));
    }

    private static Result<UploadBankStatementResponse> BuildResponse(
        int totalFiles, Company? company,
        ClassificationOutcome outcome, List<string> parseErrors)
        => Result<UploadBankStatementResponse>.Success(new UploadBankStatementResponse(
            TotalFiles:          totalFiles,
            TotalProcessed:      outcome.Classified.Count,
            DuplicatesSkipped:   outcome.TotalDuplicates,
            ReappliedToExisting: outcome.TotalReapplied,
            CompanyName:         company?.Name ?? "Sin empresa",
            PerFile:             outcome.PerFile,
            SkippedDuplicates:   outcome.SkippedDuplicates,
            ParseErrors:         parseErrors
        ));

    /// <summary>
    /// Graba el resultado durable del job para que el endpoint de polling
    /// (<c>GET /api/transactions/upload/{uploadId}/result</c>) lo pueda leer. Se serializa
    /// { value, error } como JSON crudo — el endpoint lo reenvía tal cual al frontend, sin
    /// necesidad de un tipo compartido entre el escritor (este handler) y el lector (el endpoint).
    /// </summary>
    // JsonSerializerDefaults.Web: mismo naming policy (camelCase) que usa ASP.NET Core al serializar
    // las respuestas HTTP hoy — el endpoint de polling reenvía este JSON crudo al frontend, que
    // espera camelCase (ver UploadResponse en transaction.ts).
    private static readonly JsonSerializerOptions JobResultJsonOptions = new(JsonSerializerDefaults.Web);

    private async Task SaveJobResultAsync(
        UploadBankStatementCommand command, Result<UploadBankStatementResponse> result, CancellationToken ct)
    {
        var resultJson = JsonSerializer.Serialize(
            new { value = result.Value, error = result.Error }, JobResultJsonOptions);

        _db.UploadJobResults.Add(new UploadJobResult
        {
            JobId          = command.UploadId.ToString(),
            StudioTenantId = command.StudioTenantId,
            IsSuccess      = result.IsSuccess,
            StatusCode     = result.StatusCode,
            ResultJson     = resultJson,
        });

        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Presupuesto de duplicados contra la BD: copias restantes por firma + primer match de cada firma.</summary>
    private sealed record DedupBudget(
        Dictionary<string, int> Remaining,
        Dictionary<string, BankTransaction> FirstMatch);

    /// <summary>Resultado agregado de clasificar y deduplicar el lote.</summary>
    private sealed record ClassificationOutcome(
        List<BankTransaction> Classified,
        List<FileUploadResult> PerFile,
        List<SkippedDuplicateItem> SkippedDuplicates,
        int TotalDuplicates,
        int TotalReapplied);
}
