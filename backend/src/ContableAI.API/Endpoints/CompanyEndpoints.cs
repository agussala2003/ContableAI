using ContableAI.Application.Common;
using ContableAI.Application.Features.Companies.Commands;
using ContableAI.Application.Features.Companies.Queries;
using ContableAI.Application.Features.Rules.Commands;
using ContableAI.Application.Features.Rules.Queries;
using ContableAI.API.Common;
using ContableAI.Domain.Constants;
using ContableAI.Domain.Entities;
using ContableAI.Domain.Enums;
using ContableAI.Infrastructure.Persistence;
using ContableAI.Infrastructure.Services;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ContableAI.API.Endpoints;

public static class CompanyEndpoints
{
    public static void MapCompanyEndpoints(this WebApplication app)
    {
        app.MapGet("/api/companies", async (
            ICurrentTenantService tenant,
            IMediator             mediator) =>
        {
            var result = await mediator.Send(new GetCompaniesQuery(tenant.StudioTenantId!));
            return result.ToHttpResult();
        })
        .WithName("GetCompanies")
        .WithTags("Empresas")
        .WithSummary("Listar todas las empresas activas del estudio autenticado.")
        .Produces<List<CompanyResponse>>(200);

        app.MapGet("/api/companies/{id:guid}", async (Guid id, IMediator mediator) =>
        {
            var result = await mediator.Send(new GetCompanyQuery(id));
            return result.ToHttpResult();
        })
        .WithName("GetCompany")
        .WithTags("Empresas")
        .WithSummary("Obtener una empresa por ID.")
        .Produces<CompanyResponse>(200)
        .Produces<ProblemDetails>(404);

        app.MapPost("/api/companies", async (
            CreateCompanyRequest  req,
            ICurrentTenantService tenant,
            IMediator             mediator) =>
        {
            var cmd    = new CreateCompanyCommand(req.Name, req.Cuit, req.BusinessType, req.BankAccountName, tenant.StudioTenantId ?? "ESTUDIO_DEFAULT");
            var result = await mediator.Send(cmd);
            return result.StatusCode == 201
                ? result.ToCreatedResult($"/api/companies/{result.Value?.Id}")
                : result.ToHttpResult();
        })
        .WithName("CreateCompany")
        .WithTags("Empresas")
        .WithSummary("Crear una nueva empresa.")
        .WithDescription("Body: { name, cuit, businessType (opcional), bankAccountName (opcional) }. Valida unicidad de CUIT y cuota del plan antes de persistir. Devuelve 402 si se alcanzó el límite de empresas.")
        .Produces<CompanyResponse>(201)
        .Produces<ProblemDetails>(409)
        .Produces<ProblemDetails>(402);

        app.MapPut("/api/companies/{id:guid}", async (
            Guid                 id,
            UpdateCompanyRequest req,
            IMediator            mediator) =>
        {
            var cmd    = new UpdateCompanyCommand(id, req.Name, req.BusinessType, req.SplitChequeTax, req.BankAccountName);
            var result = await mediator.Send(cmd);
            return result.ToHttpResult();
        })
        .WithName("UpdateCompany")
        .WithTags("Empresas")
        .WithSummary("Actualizar parcialmente una empresa.")
        .WithDescription("Body: { name, businessType, splitChequeTax (bool), bankAccountName }. Solo los campos no-null se actualizan. bankAccountName es la cuenta contable bancaria usada para los asientos.")
        .Produces<CompanyResponse>(200)
        .Produces<ProblemDetails>(404);

        app.MapDelete("/api/companies/{id:guid}", async (Guid id, IMediator mediator) =>
        {
            var result = await mediator.Send(new DeleteCompanyCommand(id));
            return result.IsSuccess ? Results.NoContent() : result.ToHttpResult();
        })
        .WithName("DeleteCompany")
        .WithTags("Empresas")
        .WithSummary("Dar de baja una empresa (IsActive = false).")
        .WithDescription("Soft-delete: la empresa deja de aparecer en listados pero su historial de transacciones y asientos se conserva.")
        .Produces(204)
        .Produces<ProblemDetails>(404);

        // ── Rules nested under company ────────────────────────────────────────
        app.MapGet("/api/companies/{companyId:guid}/rules", async (Guid companyId, IMediator mediator, [FromQuery] bool includeInactive = false) =>
        {
            var result = await mediator.Send(new GetCompanyRulesQuery(companyId, includeInactive));
            return result.ToHttpResult();
        })
        .WithName("GetCompanyRules")
        .WithTags("Reglas")
        .WithSummary("Listar todas las reglas de clasificación de una empresa, ordenadas por prioridad.")
        .Produces<List<RuleResponse>>(200);

        app.MapPost("/api/companies/{companyId:guid}/rules", async (
            Guid                  companyId,
            CreateRuleRequest     req,
            ICurrentTenantService tenant,
            IMediator             mediator) =>
        {
            var cmd    = new CreateCompanyRuleCommand(companyId, req.Keyword, req.TargetAccount, req.Direction, req.Priority, req.RequiresTaxMatching, tenant.StudioTenantId!);
            var result = await mediator.Send(cmd);
            return result.StatusCode == 201
                ? result.ToCreatedResult($"/api/companies/{companyId}/rules/{result.Value?.Id}")
                : result.ToHttpResult();
        })
        .WithName("CreateCompanyRule")
        .WithTags("Reglas")
        .WithSummary("Agregar una regla de clasificación a una empresa.")
        .WithDescription("Body: { keyword: string, targetAccount: string, direction: \"DEBIT\" | \"CREDIT\" | null, priority: int, requiresTaxMatching: bool }. El keyword se compara case-insensitive contra descripciones de transacciones. Incluye validación de cuota del plan.")
        .Produces<RuleResponse>(201)
        .Produces<ProblemDetails>(402)
        .Produces<RuleResponse>(201)
        .Produces<ProblemDetails>(402)
        .Produces<ProblemDetails>(404);

        //── Rule Suggestions ──────────────────────────────────────────────────
        app.MapGet("/api/companies/{companyId:guid}/suggestions", async (
            Guid companyId,
            ICurrentTenantService tenant,
            IMediator mediator) =>
        {
            var result = await mediator.Send(new GetRuleSuggestionsQuery(companyId, tenant.StudioTenantId!));
            return result.ToHttpResult();
        })
        .WithName("GetRuleSuggestions")
        .WithTags("Reglas", "IA Proactiva")
        .WithSummary("Obtener las sugerencias pendientes de reglas.")
        .Produces<List<RuleSuggestionResponse>>(200);

        app.MapPost("/api/companies/{companyId:guid}/suggestions/{id:guid}/accept", async (
            Guid companyId,
            Guid id,
            ICurrentTenantService tenant,
            IMediator mediator) =>
        {
            var result = await mediator.Send(new AcceptRuleSuggestionCommand(companyId, id, tenant.StudioTenantId!));
            return result.StatusCode == 201
                ? Results.Created($"/api/companies/{companyId}/rules/{result.Value?.Rule.Id}",
                    new { Rule = result.Value!.Rule, UpdatedTransactionCount = result.Value.UpdatedTransactionCount })
                : result.ToHttpResult();
        })
        .WithName("AcceptRuleSuggestion")
        .WithTags("Reglas", "IA Proactiva")
        .WithSummary("Aceptar una sugerencia")
        .Produces<RuleResponse>(201)
        .Produces<ProblemDetails>(402)
        .Produces<ProblemDetails>(404)
        .Produces<ProblemDetails>(409);

        app.MapPost("/api/companies/{companyId:guid}/suggestions/{id:guid}/reject", async (
            Guid companyId,
            Guid id,
            IMediator mediator) =>
        {
            var result = await mediator.Send(new RejectRuleSuggestionCommand(companyId, id));
            return result.ToHttpResult();
        })
        .WithName("RejectRuleSuggestion")
        .WithTags("Reglas", "IA Proactiva")
        .WithSummary("Rechazar una sugerencia")
        .Produces(200)
        .Produces<ProblemDetails>(404);

        app.MapPost("/api/companies/{companyId:guid}/suggestions/recalculate", async (
            Guid companyId,
            ICurrentTenantService tenant,
            ContableAIDbContext db,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger(nameof(CompanyEndpoints));
            var cutoff = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-730));

            var raw = await db.BankTransactions
                .AsNoTracking()
                .Where(t => t.CompanyId == companyId
                         && t.ClassificationSource == ClassificationSources.Manual
                         && t.AssignedAccount != null
                         && t.Date >= cutoff)
                .Select(t => new { t.TenantId, t.Description, Account = t.AssignedAccount })
                .ToListAsync(ct);

            logger.LogDebug("Recalculate suggestions: Start — Company={CompanyId}, RawCount={RawCount}", companyId, raw.Count);

            var allMappings = raw.Select(t => new
            {
                t.Description,
                Keyword = NormalizeKeyword(t.Description),
                t.Account,
            }).ToList();

            var candidateGroups = allMappings
                .GroupBy(t => new { t.Keyword, t.Account })
                .Select(g => new
                {
                    g.Key.Keyword,
                    g.Key.Account,
                    Count    = g.Count(),
                    Samples  = g.Take(5).Select(x => x.Description).ToList(),
                    TenantId = raw.First(r => r.Description == g.First().Description).TenantId,
                })
                .ToList();

            var aboveThreshold = candidateGroups
                .Where(g => !string.IsNullOrWhiteSpace(g.Keyword) && g.Count >= 3)
                .ToList();

            int newSuggestions = 0;

            foreach (var group in aboveThreshold)
            {
                if (group.Account is null) continue;

                bool ruleExists = await db.AccountingRules
                    .AnyAsync(r => r.CompanyId == companyId && r.Keyword == group.Keyword, ct);
                if (ruleExists)
                {
                    logger.LogDebug("Recalculate suggestions: Skip — Keyword={Keyword}, Reason=RuleExists", group.Keyword);
                    continue;
                }

                var existing = await db.RuleSuggestions
                    .FirstOrDefaultAsync(s => s.CompanyId == companyId && s.Keyword == group.Keyword, ct);

                if (existing != null)
                {
                    if (existing.Status == SuggestionStatus.Rejected)
                    {
                        existing.Status           = SuggestionStatus.Pending;
                        existing.SuggestedAccount = group.Account!;
                        existing.Frequency        = group.Count;
                        newSuggestions++;
                        logger.LogDebug("Recalculate suggestions: Reactivated — Keyword={Keyword}, Account={Account}, Frequency={Frequency}", group.Keyword, group.Account, group.Count);
                    }
                    else if (existing.Status == SuggestionStatus.Pending && existing.Frequency < group.Count)
                    {
                        existing.Frequency = group.Count;
                        logger.LogDebug("Recalculate suggestions: Updated — Keyword={Keyword}, NewFrequency={NewFrequency}", group.Keyword, group.Count);
                    }
                    continue;
                }

                db.RuleSuggestions.Add(new RuleSuggestion
                {
                    TenantId         = tenant.StudioTenantId!,
                    CompanyId        = companyId,
                    Keyword          = group.Keyword,
                    SuggestedAccount = group.Account,
                    Frequency        = group.Count,
                    Status           = SuggestionStatus.Pending
                });
                newSuggestions++;
                logger.LogDebug("Recalculate suggestions: Created — Keyword={Keyword}, Account={Account}, Frequency={Frequency}", group.Keyword, group.Account, group.Count);
            }

            await db.SaveChangesAsync(ct);

            logger.LogInformation("Recalculate suggestions: Done — Company={CompanyId}, NewSuggestions={NewSuggestions}", companyId, newSuggestions);

            return Results.Ok(new { NewSuggestions = newSuggestions });
        })
        .WithName("RecalculateRuleSuggestions")
        .WithTags("Reglas", "IA Proactiva")
        .WithSummary("Forzar recalculate de sugerencias de reglas para una empresa (sin esperar el job de 24h).")
        .Produces(200);
    }

    private static string NormalizeKeyword(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return string.Empty;
        var withoutBrackets = System.Text.RegularExpressions.Regex.Replace(
            description.Trim(), @"\[.*?\]|\(.*?\)", " ");
        var words = withoutBrackets.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                                   .Where(w => !w.Any(char.IsDigit))
                                   .ToArray();
        var result = string.Join(' ', words).ToUpperInvariant().Trim();
        return string.IsNullOrWhiteSpace(result) ? description.Trim().ToUpperInvariant() : result;
    }
}
