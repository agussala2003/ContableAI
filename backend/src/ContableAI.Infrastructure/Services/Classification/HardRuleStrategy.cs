using ContableAI.Domain.Common;
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
        // El criterio de coincidencia vive en KeywordMatcher (Domain): es el mismo que usan la
        // reaplicación de reglas y la aceptación de sugerencias, que filtran por SQL con el
        // patrón ILIKE equivalente. Tenerlo en un solo lugar evita que esos flujos alcancen un
        // conjunto de movimientos distinto al que clasifica este motor.
        static bool Matches(BankTransaction transaction, AccountingRule rule) =>
            KeywordMatcher.Matches(transaction.Description, rule.Keyword)
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
