using ContableAI.Application.Common;
using ContableAI.Application.Features.Companies.Commands;
using ContableAI.Application.Features.Rules.Commands;
using ContableAI.Application.Features.Rules.Queries;
using ContableAI.Domain.Entities;
using ContableAI.Domain.Enums;
using ContableAI.Infrastructure.Persistence;
using ContableAI.Infrastructure.Services;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace ContableAI.Infrastructure.Features.Rules;

// ── Obtenemos las sugerencias pendientes de la empresa ─────────────────────────────
public sealed class GetRuleSuggestionsHandler
    : IRequestHandler<GetRuleSuggestionsQuery, Result<List<RuleSuggestionResponse>>>
{
    private readonly ContableAIDbContext _db;

    public GetRuleSuggestionsHandler(ContableAIDbContext db) => _db = db;

    public async Task<Result<List<RuleSuggestionResponse>>> Handle(GetRuleSuggestionsQuery cmd, CancellationToken ct)
    {
        var suggestions = await _db.RuleSuggestions
            .AsNoTracking()
            .Where(s => s.CompanyId == cmd.CompanyId && s.Status == SuggestionStatus.Pending)
            .OrderByDescending(s => s.Frequency)
            .ThenByDescending(s => s.CreatedAt)
            .Select(s => new RuleSuggestionResponse(s.Id, s.Keyword, s.SuggestedAccount, s.Frequency, s.CreatedAt))
            .ToListAsync(ct);

        return Result<List<RuleSuggestionResponse>>.Success(suggestions);
    }
}

// ── Aceptar Sugerencia ─────────────────────────────────────────────────────────────
public sealed class AcceptRuleSuggestionHandler
    : IRequestHandler<AcceptRuleSuggestionCommand, Result<AcceptSuggestionResponse>>
{
    private readonly ContableAIDbContext _db;
    private readonly IQuotaService _quota;

    public AcceptRuleSuggestionHandler(ContableAIDbContext db, IQuotaService quota)
    {
        _db = db;
        _quota = quota;
    }

    public async Task<Result<AcceptSuggestionResponse>> Handle(AcceptRuleSuggestionCommand cmd, CancellationToken ct)
    {
        var suggestion = await _db.RuleSuggestions
            .FirstOrDefaultAsync(s => s.Id == cmd.SuggestionId && s.CompanyId == cmd.CompanyId, ct);

        if (suggestion is null) return Result<AcceptSuggestionResponse>.NotFound("Sugerencia no encontrada o no pertenece a esta empresa.");
        if (suggestion.Status != SuggestionStatus.Pending) return Result<AcceptSuggestionResponse>.Conflict("La sugerencia ya fue procesada.");

        if (!await _quota.CanAddRuleAsync(cmd.StudioTenantId, cmd.CompanyId))
            return Result<AcceptSuggestionResponse>.PaymentRequired(
                "QUOTA_EXCEEDED",
                "Alcanzaste el límite de reglas automáticas de tu plan. Actualizá para aceptar más sugerencias.");

        var rule = new AccountingRule
        {
            CompanyId           = cmd.CompanyId,
            Keyword             = suggestion.Keyword,
            TargetAccount       = suggestion.SuggestedAccount,
            Priority            = 100,
            RequiresTaxMatching = false
        };

        _db.AccountingRules.Add(rule);
        suggestion.Status = SuggestionStatus.Accepted;

        var company = await _db.Companies.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == cmd.CompanyId, ct);

        var globalRuleIds = await _db.AccountingRules
            .Where(r => r.CompanyId == null)
            .Select(r => r.Id)
            .ToListAsync(ct);

        // Build ILIKE pattern "%WORD1%WORD2%..." so digit-words that sit between non-digit
        // words in the original description don't break the match
        // (e.g. keyword "COELSA EMPRESA SA" must match "COELSA 12345 EMPRESA SA")
        var keywordPattern = "%" + string.Join("%",
            suggestion.Keyword
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(w => w.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")))
            + "%";

        var candidates = await _db.BankTransactions
            .Where(t => t.CompanyId == cmd.CompanyId
                     && t.JournalEntryId == null
                     && EF.Functions.ILike(t.Description, keywordPattern, "\\")
                     && (rule.Direction == null || t.Type == rule.Direction)
                     && (t.AssignedAccount == null
                         || t.ClassificationSource == ContableAI.Domain.Constants.ClassificationSources.Pending
                         || t.ClassificationSource == ContableAI.Domain.Constants.ClassificationSources.Manual
                         || (t.ClassificationSource == ContableAI.Domain.Constants.ClassificationSources.HardRule
                             && t.AppliedRuleId != null
                             && globalRuleIds.Contains(t.AppliedRuleId.Value))))
            .ToListAsync(ct);

        bool isChequeTaxRule = rule.TargetAccount.Equals("IMPUESTO AL CHEQUE", StringComparison.OrdinalIgnoreCase);
        string source = (isChequeTaxRule && company?.SplitChequeTax == true)
            ? ContableAI.Domain.Constants.ClassificationSources.ChequeTaxSplit
            : ContableAI.Domain.Constants.ClassificationSources.HardRule;
        float confidence = rule.RequiresTaxMatching ? 0.75f : 1.0f;

        foreach (var tx in candidates)
            tx.Assign(rule.TargetAccount, rule.Id, rule.RequiresTaxMatching, source, confidence);

        await _db.SaveChangesAsync(ct);

        var ruleResponse = new RuleResponse(rule.Id, rule.CompanyId, rule.StudioTenantId, rule.Keyword, rule.TargetAccount, null, rule.Priority, rule.RequiresTaxMatching, true);
        return Result<AcceptSuggestionResponse>.Success(new AcceptSuggestionResponse(ruleResponse, candidates.Count), 201);
    }
}

// ── Rechazar Sugerencia ─────────────────────────────────────────────────────────────
public sealed class RejectRuleSuggestionHandler
    : IRequestHandler<RejectRuleSuggestionCommand, Result<bool>>
{
    private readonly ContableAIDbContext _db;

    public RejectRuleSuggestionHandler(ContableAIDbContext db) => _db = db;

    public async Task<Result<bool>> Handle(RejectRuleSuggestionCommand cmd, CancellationToken ct)
    {
        var suggestion = await _db.RuleSuggestions
            .FirstOrDefaultAsync(s => s.Id == cmd.SuggestionId && s.CompanyId == cmd.CompanyId, ct);

        if (suggestion is null) return Result<bool>.NotFound("Sugerencia no encontrada.");

        suggestion.Status = SuggestionStatus.Rejected;
        await _db.SaveChangesAsync(ct);

        return Result<bool>.Success(true);
    }
}
