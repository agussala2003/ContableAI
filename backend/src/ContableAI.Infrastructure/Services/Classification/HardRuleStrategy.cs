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
        // Check that each keyword word appears in the description in order,
        // so "COELSA EMPRESA SA" matches "COELSA 12345 EMPRESA SA" even with digit-words in between
        static bool DescriptionMatchesKeyword(string description, string keyword)
        {
            int pos = 0;
            foreach (var word in keyword.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                int idx = description.IndexOf(word, pos, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) return false;
                pos = idx + word.Length;
            }
            return true;
        }

        bool Matches(BankTransaction transaction, AccountingRule rule) =>
            DescriptionMatchesKeyword(transaction.Description, rule.Keyword)
            && (rule.Direction is null || rule.Direction == transaction.Type);

        // Precedence: Company > Studio > System
        var companyRule = tx.CompanyId.HasValue
            ? allRules
                .Where(r => r.CompanyId == tx.CompanyId)
                .OrderBy(r => r.Priority)
                .FirstOrDefault(r => Matches(tx, r))
            : null;

        var studioRule = companyRule is null
            ? allRules
                .Where(r => r.CompanyId == null && r.StudioTenantId != null)
                .OrderBy(r => r.Priority)
                .FirstOrDefault(r => Matches(tx, r))
            : null;

        var rule = companyRule
            ?? studioRule
            ?? allRules
                .Where(r => r.CompanyId == null && r.StudioTenantId == null)
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
