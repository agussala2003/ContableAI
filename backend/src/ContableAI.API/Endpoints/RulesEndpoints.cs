using ContableAI.API.Common;
using ContableAI.Application.Features.Companies.Commands;
using ContableAI.Application.Features.Companies.Queries;
using ContableAI.Application.Features.Rules.Commands;
using ContableAI.Application.Features.Rules.Queries;
using ContableAI.Domain.Enums;
using ContableAI.Domain.Constants;
using ContableAI.Infrastructure.Persistence;
using ContableAI.Infrastructure.Services;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ContableAI.API.Endpoints;

public static class RulesEndpoints
{
    public static void MapRulesEndpoints(this WebApplication app)
    {
        app.MapPut("/api/rules/{id:guid}", async (
            Guid id,
            CreateRuleRequest req,
            ContableAIDbContext dbContext) =>
        {
            if (!await dbContext.AccountingRules.AnyAsync(r => r.Id == id))
                return Results.NotFound();

            TransactionType? direction = req.Direction?.ToUpper() switch
            {
                "DEBIT"  => TransactionType.Debit,
                "CREDIT" => TransactionType.Credit,
                _        => null
            };

            await dbContext.AccountingRules
                .Where(r => r.Id == id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Keyword,            req.Keyword)
                    .SetProperty(r => r.TargetAccount,       req.TargetAccount)
                    .SetProperty(r => r.Direction,           direction)
                    .SetProperty(r => r.Priority,            req.Priority ?? 100)
                    .SetProperty(r => r.RequiresTaxMatching, req.RequiresTaxMatching ?? false)
                );

            return Results.NoContent();
        })
        .RequireAuthorization(AuthorizationPolicies.RequireStudioOwner)
        .WithName("UpdateRule")
        .WithTags("Reglas")
        .WithSummary("Actualizar una regla de clasificación de empresa.")
        .WithDescription("Body: { keyword: string, targetAccount: string, direction: \"DEBIT\" | \"CREDIT\" | null, priority: int, requiresTaxMatching: bool }. Aplica solo a reglas de empresa (no globales).")
        .Produces(204)
        .Produces(404);

        app.MapDelete("/api/rules/{id:guid}", async (Guid id, ContableAIDbContext dbContext) =>
        {
            var rule = await dbContext.AccountingRules.FindAsync(id);
            if (rule is null) return Results.NotFound();
            dbContext.AccountingRules.Remove(rule);
            await dbContext.SaveChangesAsync();
            return Results.NoContent();
        })
        .RequireAuthorization(AuthorizationPolicies.RequireStudioOwner)
        .WithName("DeleteRule")
        .WithTags("Reglas")
        .WithSummary("Eliminar una regla de clasificación de empresa.")
        .WithDescription("Borra la regla de la base de datos de manera definitiva.")
        .Produces(204)
        .Produces(404);

        app.MapPatch("/api/rules/{id:guid}/deactivate", async (Guid id, ContableAIDbContext dbContext) =>
        {
            var rule = await dbContext.AccountingRules.FindAsync(id);
            if (rule is null) return Results.NotFound();
            rule.IsActive = false;
            await dbContext.SaveChangesAsync();
            return Results.NoContent();
        })
        .RequireAuthorization(AuthorizationPolicies.RequireStudioOwner)
        .WithName("DeactivateRule")
        .WithTags("Reglas")
        .WithSummary("Desactivar (soft-delete) una regla.")
        .Produces(204)
        .Produces(404);

        app.MapPatch("/api/rules/{id:guid}/activate", async (Guid id, ContableAIDbContext dbContext) =>
        {
            var rule = await dbContext.AccountingRules.FindAsync(id);
            if (rule is null) return Results.NotFound();
            rule.IsActive = true;
            await dbContext.SaveChangesAsync();
            return Results.NoContent();
        })
        .RequireAuthorization(AuthorizationPolicies.RequireStudioOwner)
        .WithName("ActivateRule")
        .WithTags("Reglas")
        .WithSummary("Activar una regla previamente desactivada.")
        .Produces(204)
        .Produces(404);

        app.MapPost("/api/rules/{id:guid}/reapply", async (Guid id, ContableAIDbContext dbContext) =>
        {
            var rule = await dbContext.AccountingRules
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == id);

            if (rule is null)
                return Results.NotFound("Regla no encontrada.");

            if (rule.CompanyId is null)
                return Results.BadRequest("Las reglas globales no se pueden reaplicar desde este endpoint.");

            var company = await dbContext.Companies
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == rule.CompanyId.Value);

            if (company is null)
                return Results.NotFound("Empresa de la regla no encontrada.");

            // Collect IDs of all global (company-agnostic) rules so we can identify
            // transactions that were classified by a general rule and should now be
            // superseded by this new own rule.
            var globalRuleIds = await dbContext.AccountingRules
                .Where(r => r.CompanyId == null)
                .Select(r => r.Id)
                .ToListAsync();

            var candidates = await dbContext.BankTransactions
                .Where(t => t.CompanyId == company.Id
                            && t.JournalEntryId == null
                            && t.Description.Contains(rule.Keyword)
                            && (rule.Direction == null || t.Type == rule.Direction)
                            && (
                                t.AssignedAccount == null
                                || t.ClassificationSource == ClassificationSources.Pending
                                || (t.ClassificationSource == ClassificationSources.HardRule
                                    && t.AppliedRuleId != null
                                    && globalRuleIds.Contains(t.AppliedRuleId.Value))
                            ))
                .ToListAsync();

            bool isChequeTaxRule = rule.TargetAccount.Equals("IMPUESTO AL CHEQUE", StringComparison.OrdinalIgnoreCase);
            string source = (isChequeTaxRule && company.SplitChequeTax)
                ? ClassificationSources.ChequeTaxSplit
                : ClassificationSources.HardRule;

            float confidence = rule.RequiresTaxMatching ? 0.75f : 1.0f;

            foreach (var tx in candidates)
            {
                tx.Assign(rule.TargetAccount, rule.Id, rule.RequiresTaxMatching, source, confidence);
            }

            if (candidates.Count > 0)
                await dbContext.SaveChangesAsync();

            return Results.Ok(new
            {
                RuleId = rule.Id,
                UpdatedCount = candidates.Count,
                TransactionIds = candidates.Select(t => t.Id).ToList(),
                AppliedAccount = rule.TargetAccount,
            });
        })
        .RequireAuthorization(AuthorizationPolicies.RequireStudioOwner)
        .WithName("ReapplyRule")
        .WithTags("Reglas")
        .WithSummary("Reaplicar una regla sobre movimientos sin clasificar ya cargados.")
        .WithDescription("Actualiza transacciones de la empresa de la regla con AssignedAccount null/Pending y sin asiento generado.")
        .Produces(200)
        .Produces(400)
        .Produces(404);

        app.MapGet("/api/suggestions/debug-log", () =>
        {
            var logPath = Path.Combine(Directory.GetCurrentDirectory(), "logs", "suggestions-debug.jsonl");
            if (!File.Exists(logPath))
                return Results.NotFound(new { Message = "No hay entradas de debug todavía. Probá asignar una cuenta y ejecutar Recalcular." });
            return Results.File(logPath, "application/x-ndjson", "suggestions-debug.jsonl");
        })
        .WithName("GetSuggestionsDebugLog")
        .WithTags("Reglas", "IA Proactiva")
        .WithSummary("Descarga el log de debug del sistema de sugerencias (JSONL).")
        .RequireAuthorization(p => p.RequireRole(UserRole.StudioOwner.ToString(), UserRole.SystemAdmin.ToString()))
        .Produces(200)
        .Produces(404);

        app.MapDelete("/api/suggestions/debug-log", () =>
        {
            var logPath = Path.Combine(Directory.GetCurrentDirectory(), "logs", "suggestions-debug.jsonl");
            if (File.Exists(logPath)) File.Delete(logPath);
            return Results.Ok(new { Message = "Log de debug eliminado." });
        })
        .WithName("ClearSuggestionsDebugLog")
        .WithTags("Reglas", "IA Proactiva")
        .WithSummary("Limpia el log de debug para empezar una sesión de prueba desde cero.")
        .RequireAuthorization(AuthorizationPolicies.RequireStudioOwner)
        .Produces(200);

        // Suggestion endpoints (GET/accept/reject) -> CompanyEndpoints.cs

        // ── Studio Rules ──────────────────────────────────────────────────────
        app.MapGet("/api/studio/rules", async (
            ISender               sender,
            ICurrentTenantService tenant,
            [FromQuery] bool      includeInactive = false) =>
        {
            var result = await sender.Send(new GetStudioRulesQuery(tenant.StudioTenantId!, includeInactive));
            return result.ToHttpResult();
        })
        .WithName("GetStudioRules")
        .WithTags("Reglas", "Estudio")
        .WithSummary("Listar reglas globales del estudio.")
        .Produces<List<RuleResponse>>(200);

        app.MapPost("/api/studio/rules", async (
            CreateRuleRequest     req,
            ISender               sender,
            ICurrentTenantService tenant) =>
        {
            var cmd    = new CreateStudioRuleCommand(tenant.StudioTenantId!, req.Keyword, req.TargetAccount, req.Direction, req.Priority, req.RequiresTaxMatching);
            var result = await sender.Send(cmd);
            return result.StatusCode == 201
                ? result.ToCreatedResult($"/api/studio/rules/{result.Value?.Id}")
                : result.ToHttpResult();
        })
        .RequireAuthorization(AuthorizationPolicies.RequireStudioOwner)
        .WithName("CreateStudioRule")
        .WithTags("Reglas", "Estudio")
        .WithSummary("Crear una regla global de estudio.")
        .Produces<RuleResponse>(201)
        .Produces<ProblemDetails>(400);
    }
}
