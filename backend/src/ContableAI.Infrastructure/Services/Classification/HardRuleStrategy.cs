using ContableAI.Domain.Constants;
using ContableAI.Domain.Entities;

namespace ContableAI.Infrastructure.Services.Classification;

/// <summary>
/// Strategy 1 — Hard accounting rules.
/// Evaluates rules with strict precedence: first company-specific rules, then global rules.
/// Execution stops on the first match found in that order.
/// </summary>
public sealed class HardRuleStrategy : IClassificationStrategy
{
    public Task<bool> TryClassifyAsync(
        BankTransaction               tx,
        IReadOnlyList<AccountingRule> allRules,
        bool                          splitChequeTax,
        CancellationToken             ct = default)
    {
        bool Matches(BankTransaction transaction, AccountingRule rule) =>
            transaction.Description.Contains(rule.Keyword, StringComparison.OrdinalIgnoreCase)
            && (rule.Direction is null || rule.Direction == transaction.Type);

        var companyRule = tx.CompanyId.HasValue
            ? allRules
                .Where(r => r.CompanyId == tx.CompanyId)
                .OrderBy(r => r.Priority)
                .FirstOrDefault(r => Matches(tx, r))
            : null;

        var rule = companyRule
            ?? allRules
                .Where(r => r.CompanyId == null)
                .OrderBy(r => r.Priority)
                .FirstOrDefault(r => Matches(tx, r));

        if (rule is null)
            return Task.FromResult(false);

        bool isChequeTaxRule = rule.TargetAccount.Equals(
            "IMPUESTO AL CHEQUE", StringComparison.OrdinalIgnoreCase);

        string source = (isChequeTaxRule && splitChequeTax)
            ? ClassificationSources.ChequeTaxSplit
            : ClassificationSources.HardRule;

        float confidence = rule.RequiresTaxMatching ? 0.75f : 1.0f;
        tx.Assign(rule.TargetAccount, rule.Id, rule.RequiresTaxMatching, source, confidence);

        return Task.FromResult(true);
    }
}
