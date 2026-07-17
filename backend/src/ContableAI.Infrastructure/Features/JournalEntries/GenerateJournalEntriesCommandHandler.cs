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
            .ToDictionaryAsync(
                c => c.Id,
                c => new CompanyBankAccounts(c.BankAccountName, c.UsdBankAccountName),
                cancellationToken);

        // La cuenta en pesos es requisito para asentar cualquier movimiento ARS. Se mantiene el
        // corte total (return) cuando falta, pero acotado a las empresas que efectivamente tienen
        // movimientos en pesos en este lote (una empresa que solo opera en USD no debe frenar por
        // no tener cuenta en pesos configurada).
        var arsCompanyIds = transactions
            .Where(t => t.Currency == Currencies.Ars)
            .Select(t => t.CompanyId!.Value)
            .Distinct()
            .ToList();

        var missingBank = arsCompanyIds
            .Where(id => !companiesMap.TryGetValue(id, out var acc) || string.IsNullOrWhiteSpace(acc.Ars))
            .ToList();
        if (missingBank.Count > 0)
        {
            var names = await _dbContext.Companies
                .AsNoTracking()
                .Where(c => missingBank.Contains(c.Id))
                .Select(c => c.Name)
                .ToListAsync(cancellationToken);
            _logger.LogWarning(
                "Cuenta bancaria en pesos no configurada para: {Companies}. Editá la empresa y completá el campo 'Nombre de cuenta bancaria' antes de asentar.",
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
        var skippedMissingUsdAccount = 0;

        // Vouchers de cruces múltiples AFIP (un débito bancario = N VEPs): el asiento se
        // desglosa con una línea por impuesto, así que se precargan en bloque.
        var comboTxIds = transactions
            .Where(t => t.ClassificationSource == ClassificationSources.AfipComboMatch)
            .Select(t => t.Id)
            .ToList();

        var comboVouchersByTx = comboTxIds.Count == 0
            ? new Dictionary<Guid, List<AfipVoucher>>()
            : (await _dbContext.AfipVouchers
                .AsNoTracking()
                .Where(v => v.MatchedTransactionId != null && comboTxIds.Contains(v.MatchedTransactionId.Value))
                .ToListAsync(cancellationToken))
              .GroupBy(v => v.MatchedTransactionId!.Value)
              .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var tx in transactions)
        {
            JournalEntry entry;
            var accounts = companiesMap[tx.CompanyId!.Value];

            // Contrapartida por moneda: USD → cuenta en dólares; ARS → cuenta en pesos.
            // Si el movimiento es en dólares y la empresa no configuró su cuenta USD, se omite
            // este movimiento (warning claro) sin frenar el resto del lote.
            if (!TryResolveBankAccount(tx.Currency, accounts.Ars, accounts.Usd, out var bankAccount))
            {
                _logger.LogWarning(
                    "Movimiento {TxId} en {Currency} omitido: la empresa {CompanyId} no tiene configurada la cuenta bancaria en esa moneda. " +
                    "Completá la cuenta bancaria en dólares en la ficha de la empresa y reintentá.",
                    tx.Id, tx.Currency, tx.CompanyId);
                skippedMissingUsdAccount++;
                continue;
            }

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
                    Currency          = tx.Currency,
                    Lines             = projectedLines,
                };
            }
            else if (tx.ClassificationSource == ClassificationSources.AfipComboMatch
                     && comboVouchersByTx.TryGetValue(tx.Id, out var comboVouchers)
                     && comboVouchers.Count >= 2
                     && comboVouchers.Sum(v => v.Amount) == tx.Amount)
            {
                // Un pago agrupado de ARCA: una línea de débito por impuesto (VEPs con el
                // mismo impuesto se consolidan) contra el banco por el total del movimiento.
                projectedLines = comboVouchers
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

                entry = new JournalEntry
                {
                    Date              = tx.Date,
                    Description       = tx.Description,
                    CompanyId         = tx.CompanyId,
                    BankTransactionId = tx.Id,
                    Currency          = tx.Currency,
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
                    Currency          = tx.Currency,
                    Lines             = projectedLines,
                };
            }

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
                await _dbContext.SaveChangesAsync(cancellationToken);
                totalGenerated += pendingEntries.Count;
                pendingEntries.Clear();
            }
        }

        if (pendingEntries.Count > 0)
            _dbContext.JournalEntries.AddRange(pendingEntries);

        totalGenerated += pendingEntries.Count;
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Background job finished: Generated {Generated}, Skipped {Skipped}, MissingUsdAccount {MissingUsd}",
            totalGenerated, duplicatesSkipped, skippedMissingUsdAccount);
    }

    /// <summary>Cuentas bancarias de una empresa por moneda (pesos y dólares).</summary>
    private sealed record CompanyBankAccounts(string? Ars, string? Usd);

    /// <summary>
    /// Resuelve la cuenta bancaria de contrapartida según la moneda del movimiento: USD usa la
    /// cuenta en dólares de la empresa; cualquier otra moneda usa la cuenta en pesos. Devuelve
    /// <c>false</c> si la cuenta correspondiente no está configurada (para omitir el movimiento
    /// sin frenar el lote). Método puro para poder testear la selección sin base de datos.
    /// </summary>
    internal static bool TryResolveBankAccount(string currency, string? arsAccount, string? usdAccount, out string account)
    {
        var resolved = currency == Currencies.Usd ? usdAccount : arsAccount;
        account = string.IsNullOrWhiteSpace(resolved) ? string.Empty : resolved;
        return account.Length > 0;
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
