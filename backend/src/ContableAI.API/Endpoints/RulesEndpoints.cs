using ContableAI.API.Common;
using ContableAI.Application.Features.Companies.Commands;
using ContableAI.Application.Features.Companies.Queries;
using ContableAI.Application.Features.Rules.Commands;
using ContableAI.Application.Features.Rules.Queries;
using ContableAI.Domain.Common;
using ContableAI.Domain.Enums;
using ContableAI.Domain.Constants;
using ContableAI.Infrastructure.Persistence;
using ContableAI.Infrastructure.Services;
using Hangfire;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

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

        // El POST /api/rules/{id}/reapply sincrónico se retiró en la v1.1: corría al guardar la
        // regla, alcanzaba solo a los movimientos sin clasificar y convivía con este endpoint con
        // semántica distinta. La reaplicación retroactiva tiene ahora un único camino, explícito y
        // con preview: /reapply-async.
        app.MapPost("/api/rules/{id:guid}/reapply-async", async (
            Guid                  id,
            HttpContext           httpContext,
            ICurrentTenantService tenant,
            ISender               sender,
            ContableAIDbContext   dbContext,
            IBackgroundJobClient  backgroundJobClient,
            [FromQuery] bool      dryRun = false) =>
        {
            var email = httpContext.User.FindFirst(ClaimTypes.Email)?.Value
                     ?? httpContext.User.FindFirst("email")?.Value
                     ?? "desconocido";

            var command = new ReapplyRuleCommand(id, tenant.StudioTenantId!, email, dryRun);

            // El preview corre síncrono: es solo lectura y su costo lo acota el prefiltro por
            // keyword, así que el modal puede mostrar el impacto sin esperar a un job.
            if (dryRun)
            {
                var preview = await sender.Send(command);
                return preview.ToHttpResult();
            }

            // Validación barata ANTES de encolar (mismo criterio que /journal-entries/generate):
            // sin esto, una regla inexistente o de estudio devolvería 202 y fallaría en silencio
            // dentro del job. El filtro global de AccountingRule ya acota por estudio.
            var rule = await dbContext.AccountingRules.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == id);

            if (rule is null)
                return Results.NotFound("Regla no encontrada.");

            if (rule.CompanyId is null)
                return Results.Problem(
                    title:      "La regla no es de empresa",
                    detail:     "La reaplicación a movimientos históricos solo está disponible para reglas propias de una empresa.",
                    statusCode: 422);

            var jobId = backgroundJobClient.Enqueue<ISender>(s => s.Send(command, default));

            return Results.Accepted(value: new
            {
                JobId   = jobId,
                Message = "Reaplicación iniciada en segundo plano. Podés seguir trabajando mientras termina.",
            });
        })
        .RequireAuthorization(AuthorizationPolicies.RequireStudioOwner)
        .WithName("ReapplyRuleAsync")
        .WithTags("Reglas")
        .WithSummary("Reaplicar una regla sobre TODOS los movimientos históricos que coincidan (sobrescribe).")
        .WithDescription("A diferencia de /reapply, sobrescribe la cuenta asignada aunque haya sido puesta a mano. Nunca toca movimientos ya asentados, de períodos cerrados ni provenientes de un cruce múltiple AFIP. Con dryRun=true devuelve 200 con el impacto sin escribir; sin él encola un job de Hangfire y devuelve 202 con el jobId para pollear en /api/jobs/{jobId}/status.")
        .Produces(202)
        .Produces(200)
        .Produces(404)
        .Produces(422);

        // No recibe ICurrentTenantService: el alcance por estudio lo resuelven íntegramente los
        // Global Query Filters de AccountingRule y Company (Epic D).
        app.MapPost("/api/rules/{id:guid}/promote-to-studio", async (
            Guid                id,
            ContableAIDbContext dbContext,
            [FromQuery] bool    dryRun = false) =>
        {
            // El Global Query Filter de AccountingRule (Epic D) ya acota por estudio: una regla de
            // otro estudio devuelve null → 404, sin confirmar que exista.
            var rule = await dbContext.AccountingRules
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == id);

            if (rule is null)
                return Results.NotFound("Regla no encontrada.");

            if (rule.CompanyId is null)
                return Results.Problem(
                    title:      "La regla no es de empresa",
                    detail:     "Solo se pueden promover reglas propias de una empresa; esta ya aplica a nivel estudio o sistema.",
                    statusCode: 422);

            // GUARDA CRÍTICA: CompanyId null + StudioTenantId null es, por definición, una regla de
            // SISTEMA — visible para todos los estudios de la plataforma. Si la regla no tiene su
            // estudio estampado, vaciarle CompanyId la publicaría fuera del tenant. El filtro global
            // ya garantiza que, si llegamos acá, el estudio es el del usuario; esto cubre el dato
            // anómalo (backfill incompleto) antes de escribir.
            if (string.IsNullOrWhiteSpace(rule.StudioTenantId))
                return Results.Problem(
                    title:      "Regla sin estudio propietario",
                    detail:     "La regla no tiene estudio asignado y no puede promoverse sin exponerla a otros estudios.",
                    statusCode: 422);

            // Empresas alcanzadas: todas las activas del estudio (el filtro global las acota).
            var studioCompanies = await dbContext.Companies
                .AsNoTracking()
                .Where(c => c.IsActive)
                .Select(c => new { c.Id, c.Name })
                .ToListAsync();

            // Al promoverla, la regla BAJA de precedencia (Empresa > Estudio > Sistema): cualquier
            // empresa con una regla propia de keyword solapado va a seguir usando la suya. Se
            // traen las reglas de empresa del estudio (conjunto acotado por cuota) y el solapamiento
            // se evalúa en memoria: la normalización + contención mutua no tiene traducción directa
            // a SQL, y hacerla a mano divergiría del criterio que ya muestra la grilla.
            var siblingRules = await dbContext.AccountingRules
                .AsNoTracking()
                .Where(r => r.Id != id && r.CompanyId != null && r.IsActive)
                .Select(r => new { r.CompanyId, r.Keyword, r.Direction })
                .ToListAsync();

            var companyNames = studioCompanies.ToDictionary(c => c.Id, c => c.Name);

            var conflicts = siblingRules
                .Where(r => companyNames.ContainsKey(r.CompanyId!.Value)
                         && RuleConflict.KeywordsOverlap(r.Keyword, rule.Keyword)
                         && RuleConflict.DirectionsCompatible(r.Direction, rule.Direction))
                .Select(r => new
                {
                    CompanyId   = r.CompanyId!.Value,
                    CompanyName = companyNames[r.CompanyId!.Value],
                    r.Keyword,
                    Direction   = r.Direction?.ToString(),
                })
                .OrderBy(c => c.CompanyName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.Keyword, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (!dryRun)
            {
                // ExecuteUpdate y no una mutación por tracking: CompanyId y StudioTenantId son
                // `init`, y sobre todo así se preserva el Id de la regla — recrearla dejaría
                // huérfano el AppliedRuleId de todos los movimientos ya clasificados con ella.
                // StudioTenantId se reescribe explícitamente (aunque ya sea el correcto) para que
                // el invariante "una regla sin empresa siempre conserva su estudio" quede en el
                // mismo UPDATE que vacía CompanyId, y no dependa de un backfill previo.
                var studioTenantId = rule.StudioTenantId;

                await dbContext.AccountingRules
                    .Where(r => r.Id == id && r.CompanyId != null)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(r => r.CompanyId,      (Guid?)null)
                        .SetProperty(r => r.StudioTenantId, studioTenantId));
            }

            return Results.Ok(new
            {
                RuleId               = rule.Id,
                rule.Keyword,
                rule.TargetAccount,
                DryRun               = dryRun,
                AffectedCompanies    = studioCompanies.Count,
                ConflictingCompanies = conflicts.Select(c => c.CompanyId).Distinct().Count(),
                Conflicts            = conflicts,
            });
        })
        .RequireAuthorization(AuthorizationPolicies.RequireStudioOwner)
        .WithName("PromoteRuleToStudio")
        .WithTags("Reglas", "Estudio")
        .WithSummary("Promover una regla de empresa a regla de estudio.")
        .WithDescription("Cambia el alcance de la regla para que aplique a todas las empresas del estudio, conservando su Id (y por lo tanto la trazabilidad de los movimientos ya clasificados). Query param: dryRun=true devuelve el preview (empresas alcanzadas y conflictos por keyword solapado) sin escribir.")
        .Produces(200)
        .Produces(404)
        .Produces(422);

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
