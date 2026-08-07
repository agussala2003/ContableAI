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
    private const int BatchSize = 500;

    private readonly ContableAIDbContext _dbContext;
    private readonly ILogger<GenerateJournalEntriesCommandHandler> _logger;

    public GenerateJournalEntriesCommandHandler(ContableAIDbContext dbContext, ILogger<GenerateJournalEntriesCommandHandler> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    /// <summary>
    /// Orquesta la generación de asientos para un lote de movimientos: carga los movimientos
    /// pendientes, valida período cerrado y cuentas configuradas, y genera los asientos aplicando
    /// la deduplicación por firma. Cada fase vive en un método propio para que este flujo se lea
    /// de arriba a abajo.
    /// </summary>
    public async Task Handle(GenerateJournalEntriesCommand request, CancellationToken cancellationToken)
    {
        if (request.TransactionIds == null || request.TransactionIds.Count == 0)
            return;

        var studioCompanyIds = await LoadStudioCompanyIdsAsync(request, cancellationToken);
        var transactions     = await LoadPendingTransactionsAsync(request, studioCompanyIds, cancellationToken);
        if (transactions.Count == 0)
            return;

        if (await HasClosedPeriodConflictAsync(request, transactions, cancellationToken))
            return;

        var contraAccounts      = await LoadContraAccountsAsync(transactions, cancellationToken);
        var signatureToEntryIds = await LoadExistingSignatureMapAsync(transactions, cancellationToken);
        var comboVouchersByTx   = await LoadComboVouchersAsync(transactions, cancellationToken);

        await GenerateEntriesAsync(transactions, contraAccounts, signatureToEntryIds, comboVouchersByTx, cancellationToken);
    }

    // ── Carga de datos ──────────────────────────────────────────────────────────

    private async Task<List<Guid>> LoadStudioCompanyIdsAsync(GenerateJournalEntriesCommand request, CancellationToken ct)
        => await _dbContext.Companies
            .AsNoTracking()
            .Where(c => c.StudioTenantId == request.StudioTenantId && c.IsActive)
            .Select(c => c.Id)
            .ToListAsync(ct);

    private async Task<List<BankTransaction>> LoadPendingTransactionsAsync(
        GenerateJournalEntriesCommand request, List<Guid> studioCompanyIds, CancellationToken ct)
        => await _dbContext.BankTransactions
            .Where(t => request.TransactionIds.Contains(t.Id)
                     && t.CompanyId.HasValue
                     && studioCompanyIds.Contains(t.CompanyId.Value)
                     && t.AssignedAccount != null
                     && t.JournalEntryId == null)
            .ToListAsync(ct);

    /// <summary>
    /// Contrapartida contable de cada cuenta bancaria presente en el lote, indexada por su id.
    ///
    /// F1.c: la contrapartida dejó de depender de la moneda del movimiento y pasó a ser un dato de
    /// la CUENTA. Una empresa puede tener varias cuentas en la misma moneda y cada una asienta
    /// contra la suya; antes, todas las de pesos compartían el único string de la empresa.
    /// </summary>
    private async Task<Dictionary<Guid, string>> LoadContraAccountsAsync(
        List<BankTransaction> transactions, CancellationToken ct)
    {
        var bankAccountIds = transactions
            .Where(t => t.BankAccountId.HasValue)
            .Select(t => t.BankAccountId!.Value)
            .Distinct()
            .ToList();

        if (bankAccountIds.Count == 0)
            return [];

        // IgnoreQueryFilters: corre como job de Hangfire (sin HttpContext), y el alcance ya lo
        // acotan las empresas del estudio resueltas más arriba.
        return await _dbContext.BankAccounts
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(a => bankAccountIds.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, a => a.ContraAccountName, ct);
    }

    /// <summary>Asientos ya existentes en el rango del lote, agrupados por firma para deduplicar.</summary>
    private async Task<Dictionary<string, Queue<Guid>>> LoadExistingSignatureMapAsync(
        List<BankTransaction> transactions, CancellationToken ct)
    {
        var companyIds = transactions.Select(t => t.CompanyId!.Value).Distinct().ToList();
        var minTxDate = transactions.Min(t => t.Date);
        var maxTxDate = transactions.Max(t => t.Date);

        var existingEntries = await _dbContext.JournalEntries
            .AsNoTracking()
            .Include(j => j.Lines)
            .Where(j => j.CompanyId.HasValue
                     && companyIds.Contains(j.CompanyId.Value)
                     && j.Date >= minTxDate
                     && j.Date <= maxTxDate)
            .ToListAsync(ct);

        return existingEntries
            .GroupBy(BuildEntrySignature)
            .ToDictionary(
                g => g.Key,
                g => new Queue<Guid>(g.Select(e => e.Id)),
                StringComparer.Ordinal);
    }

    /// <summary>
    /// Vouchers de cruces múltiples AFIP (un débito bancario = N VEPs) por movimiento: el asiento
    /// se desglosa con una línea por impuesto, así que se precargan en bloque.
    /// </summary>
    private async Task<Dictionary<Guid, List<AfipVoucher>>> LoadComboVouchersAsync(
        List<BankTransaction> transactions, CancellationToken ct)
    {
        var comboTxIds = transactions
            .Where(t => t.ClassificationSource == ClassificationSources.AfipComboMatch)
            .Select(t => t.Id)
            .ToList();

        if (comboTxIds.Count == 0)
            return new Dictionary<Guid, List<AfipVoucher>>();

        return (await _dbContext.AfipVouchers
                .AsNoTracking()
                .Where(v => v.MatchedTransactionId != null && comboTxIds.Contains(v.MatchedTransactionId.Value))
                .ToListAsync(ct))
            .GroupBy(v => v.MatchedTransactionId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());
    }

    // ── Validaciones (cortan la generación del lote) ────────────────────────────

    private async Task<bool> HasClosedPeriodConflictAsync(
        GenerateJournalEntriesCommand request, List<BankTransaction> transactions, CancellationToken ct)
    {
        var closedPeriods = await _dbContext.ClosedPeriods
            .AsNoTracking()
            .Where(p => p.StudioTenantId == request.StudioTenantId)
            .Select(p => new { p.Year, p.Month })
            .ToListAsync(ct);

        if (closedPeriods.Count == 0)
            return false;

        var closedSet = closedPeriods.Select(p => (p.Year, p.Month)).ToHashSet();
        if (transactions.Any(t => closedSet.Contains((t.Date.Year, t.Date.Month))))
        {
            _logger.LogWarning("Período cerrado detectado en Hangfire. Cancelando generación.");
            return true;
        }

        return false;
    }

    // ── Generación ──────────────────────────────────────────────────────────────

    private async Task GenerateEntriesAsync(
        List<BankTransaction> transactions,
        Dictionary<Guid, string> contraAccounts,
        Dictionary<string, Queue<Guid>> signatureToEntryIds,
        Dictionary<Guid, List<AfipVoucher>> comboVouchersByTx,
        CancellationToken ct)
    {
        var pendingEntries = new List<JournalEntry>(BatchSize);
        var totalGenerated = 0;
        var duplicatesSkipped = 0;
        var skippedMissingContraAccount = 0;

        foreach (var tx in transactions)
        {
            // La contrapartida es la de la CUENTA BANCARIA del movimiento. Se omite el movimiento
            // (sin frenar el resto del lote) cuando no tiene cuenta asignada —movimiento legacy
            // anterior al alta de cuentas— o cuando la cuenta es provisional, es decir todavía sin
            // contrapartida cargada. El endpoint de generación corta antes con un 422 accionable;
            // esto es la red de seguridad del lado del job, que no puede responder HTTP.
            if (!TryResolveContraAccount(tx.BankAccountId, contraAccounts, out var bankAccount))
            {
                _logger.LogWarning(
                    "Movimiento {TxId} omitido: su cuenta bancaria ({BankAccountId}) no tiene contrapartida contable configurada. " +
                    "Completala en la ficha de la empresa, pestaña Cuentas Bancarias, y reintentá.",
                    tx.Id, tx.BankAccountId);
                skippedMissingContraAccount++;
                continue;
            }

            comboVouchersByTx.TryGetValue(tx.Id, out var comboVouchers);
            var projectedLines = ProjectLines(tx, bankAccount, comboVouchers);
            var entry = CreateEntry(tx, projectedLines);

            var signature = BuildEntrySignature(tx.CompanyId, tx.Date, tx.Description, tx.Currency, projectedLines);
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
                await _dbContext.SaveChangesAsync(ct);
                totalGenerated += pendingEntries.Count;
                pendingEntries.Clear();
            }
        }

        if (pendingEntries.Count > 0)
            _dbContext.JournalEntries.AddRange(pendingEntries);

        totalGenerated += pendingEntries.Count;
        await _dbContext.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Background job finished: Generated {Generated}, Skipped {Skipped}, MissingContraAccount {MissingContra}",
            totalGenerated, duplicatesSkipped, skippedMissingContraAccount);
    }

    /// <summary>
    /// Proyecta las líneas de partida doble de un movimiento según su tipo: split 50/50 de impuesto
    /// al cheque, desglose por impuesto de un combo AFIP (una línea por impuesto contra el banco),
    /// o el caso genérico (cuenta asignada vs. banco según débito/crédito). Método puro.
    /// </summary>
    private static List<JournalEntryLine> ProjectLines(
        BankTransaction tx, string bankAccount, IReadOnlyList<AfipVoucher>? comboVouchers)
    {
        if (tx.ClassificationSource == ClassificationSources.ChequeTaxSplit)
        {
            var half1 = Math.Round(tx.Amount / 2, 2);
            var half2 = tx.Amount - half1;

            return
            [
                new JournalEntryLine { Account = "Impuesto a los Débitos Bancarios",  Amount = half1,     IsDebit = true  },
                new JournalEntryLine { Account = "Impuesto a los Créditos Bancarios", Amount = half2,     IsDebit = true  },
                new JournalEntryLine { Account = bankAccount,                          Amount = tx.Amount, IsDebit = false },
            ];
        }

        if (tx.ClassificationSource == ClassificationSources.AfipComboMatch
            && comboVouchers is { Count: >= 2 }
            && comboVouchers.Sum(v => v.Amount) == tx.Amount)
        {
            // Un pago agrupado de ARCA: una línea de débito por impuesto (VEPs con el mismo
            // impuesto se consolidan) contra el banco por el total del movimiento.
            return comboVouchers
                .GroupBy(v => v.TaxName)
                .Select(g => new JournalEntryLine
                {
                    Account = g.Key,
                    Amount  = g.Sum(v => v.Amount),
                    IsDebit = true,
                })
                .OrderByDescending(l => l.Amount)
                .Append(new JournalEntryLine { Account = bankAccount, Amount = tx.Amount, IsDebit = false })
                .ToList();
        }

        bool isDebit = tx.Type == TransactionType.Debit;
        return
        [
            new JournalEntryLine { Account = isDebit ? tx.AssignedAccount! : bankAccount, Amount = tx.Amount, IsDebit = true  },
            new JournalEntryLine { Account = isDebit ? bankAccount : tx.AssignedAccount!, Amount = tx.Amount, IsDebit = false },
        ];
    }

    private static JournalEntry CreateEntry(BankTransaction tx, List<JournalEntryLine> lines) => new()
    {
        Date              = tx.Date,
        Description       = tx.Description,
        CompanyId         = tx.CompanyId,
        BankTransactionId = tx.Id,
        Currency          = tx.Currency,
        // Desnormalizado desde el movimiento: el libro diario se filtra y exporta por cuenta sin
        // joinear a BankTransactions (mismo criterio que Currency).
        BankAccountId     = tx.BankAccountId,
        Lines             = lines,
    };

    /// <summary>
    /// Resuelve la contrapartida contable a partir de la cuenta bancaria del movimiento.
    ///
    /// Devuelve <c>false</c> en los tres casos que impiden asentar, para que el llamador omita el
    /// movimiento sin frenar el lote:
    ///   · el movimiento no tiene cuenta bancaria asignada (legacy previo al alta de cuentas);
    ///   · la cuenta no está en el mapa (fue borrada, o no pertenece al lote cargado);
    ///   · la cuenta es provisional: existe y recibe movimientos, pero todavía no tiene
    ///     contrapartida contable cargada.
    ///
    /// Método puro para poder testear la resolución sin base de datos.
    /// </summary>
    internal static bool TryResolveContraAccount(
        Guid? bankAccountId, IReadOnlyDictionary<Guid, string> contraAccounts, out string account)
    {
        account = string.Empty;

        if (bankAccountId is null)
            return false;

        if (!contraAccounts.TryGetValue(bankAccountId.Value, out var resolved))
            return false;

        if (string.IsNullOrWhiteSpace(resolved))
            return false;

        account = resolved.Trim();
        return true;
    }

    private static string BuildEntrySignature(JournalEntry entry)
        => BuildEntrySignature(entry.CompanyId, entry.Date, entry.Description, entry.Currency, entry.Lines);

    private static string BuildEntrySignature(Guid? companyId, DateOnly date, string description, string currency, IEnumerable<JournalEntryLine> lines)
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
            currency,
            normalizedDescription,
            lineSignature);
    }

    private static string NormalizeDescription(string description)
        => string.Join(' ', (description ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToUpperInvariant();
}
