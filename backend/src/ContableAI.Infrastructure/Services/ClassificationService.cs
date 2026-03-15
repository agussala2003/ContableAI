using ContableAI.Domain.Entities;
using ContableAI.Infrastructure.Services.Classification;

namespace ContableAI.Infrastructure.Services;

public interface IClassificationService
{
    /// <summary>
    /// Classifies a bank transaction using the HardRule strategy.
    /// If no rule matches, the transaction remains as Pending.
    /// Rules must be pre-loaded by the caller to avoid N+1 queries in batch processing.
    /// </summary>
    Task<BankTransaction> ClassifyAsync(
        BankTransaction               tx,
        IReadOnlyList<AccountingRule> allRules,
        bool                          splitChequeTax = false,
        CancellationToken             ct = default);
}

/// <summary>
/// Classifies bank transactions using the HardRule strategy only.
/// Unmatched transactions remain as Pending for manual review.
/// </summary>
public sealed class ClassificationService : IClassificationService
{
    // Corporate card payment patterns that require manual breakdown.
    private static readonly string[] CardKeywords =
    [
        "PAGO TARJETA", "PAGO VISA", "PAGO MASTER", "PAGO AMEX", "AMERICAN EXPRESS",
        "PAGO NARANJA", "PAGO CABAL", "PGO TARJ", "PGO TAR", "PAGO DEBITO AUTO VISA",
        "PAGO AUTOMATICO VISA", "PAGO AUTO TARJETA",
    ];

    private static bool IsCardPayment(string description) =>
        CardKeywords.Any(k => description.Contains(k, StringComparison.OrdinalIgnoreCase));

    private readonly HardRuleStrategy _hardRule;

    public ClassificationService(HardRuleStrategy hardRule)
    {
        _hardRule = hardRule;
    }

    public async Task<BankTransaction> ClassifyAsync(
        BankTransaction               tx,
        IReadOnlyList<AccountingRule> allRules,
        bool                          splitChequeTax = false,
        CancellationToken             ct = default)
    {
        if (IsCardPayment(tx.Description))
            tx.MarkNeedsBreakdown();

        await _hardRule.TryClassifyAsync(tx, allRules, splitChequeTax, ct);
        return tx;
    }
}