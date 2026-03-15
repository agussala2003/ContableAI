using ContableAI.Domain.Entities;

namespace ContableAI.Infrastructure.Services.Classification;

/// <summary>
/// Contract for a single classification strategy.
/// Each strategy attempts to assign an account to the transaction.
/// Returns <c>true</c> if the classification was handled (pipeline stops),
/// <c>false</c> if the next strategy should be tried.
/// </summary>
public interface IClassificationStrategy
{
    /// <summary>
    /// Attempts to classify <paramref name="tx"/>.
    /// </summary>
    /// <param name="tx">Transaction to classify (mutated in-place on success).</param>
    /// <param name="allRules">Pre-loaded rules (company first, then global), ordered by priority.</param>
    /// <param name="splitChequeTax">Whether the owning company uses ChequeTax split mode.</param>
    /// <returns><c>true</c> if this strategy assigned an account; otherwise <c>false</c>.</returns>
    Task<bool> TryClassifyAsync(
        BankTransaction                tx,
        IReadOnlyList<AccountingRule>  allRules,
        bool                           splitChequeTax,
        CancellationToken              ct = default);
}
