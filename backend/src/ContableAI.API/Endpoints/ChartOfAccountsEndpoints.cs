using ContableAI.Domain.Entities;
using ContableAI.Infrastructure.Persistence;
using ContableAI.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ContableAI.API.Endpoints;

public static class ChartOfAccountsEndpoints
{
    public static void MapChartOfAccountsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/chart-of-accounts", async (
            ICurrentTenantService currentTenant,
            ContableAIDbContext   dbContext) =>
        {
            Guid.TryParse(currentTenant.StudioTenantId, out var studioGuid);

            var accounts = await dbContext.ChartOfAccounts
                .AsNoTracking()
                .Where(a => a.StudioTenantId == null || a.StudioTenantId == studioGuid)
                .OrderBy(a => a.StudioTenantId == null ? 0 : 1) // globales primero
                .ThenBy(a => a.Name)
                .Select(a => new { a.Id, a.Name, IsGlobal = a.StudioTenantId == null })
                .ToListAsync();

            return Results.Ok(accounts);
        })
        .WithName("GetChartOfAccounts")
        .WithTags("Plan de Cuentas")
        .WithSummary("Listar todas las cuentas contables (globales + propias del estudio).")
        .WithDescription("Devuelve { id, name, isGlobal }. Las cuentas globales (isGlobal = true) aparecen primero. Ambos grupos ordenados alfabéticamente.")
        .Produces(200);

        app.MapPost("/api/chart-of-accounts", async (
            CreateChartOfAccountRequest req,
            ICurrentTenantService        currentTenant,
            ContableAIDbContext          dbContext) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest("El nombre de la cuenta es obligatorio.");

            if (!Guid.TryParse(currentTenant.StudioTenantId, out var studioGuid))
                return Results.Unauthorized();

            var existing = await dbContext.ChartOfAccounts
                .AnyAsync(a => a.Name == req.Name.Trim()
                            && (a.StudioTenantId == null || a.StudioTenantId == studioGuid));

            if (existing)
                return Results.Conflict("Ya existe una cuenta con ese nombre.");

            var account = new ChartOfAccount
            {
                Name           = req.Name.Trim(),
                StudioTenantId = studioGuid,
            };

            dbContext.ChartOfAccounts.Add(account);
            await dbContext.SaveChangesAsync();

            return Results.Created($"/api/chart-of-accounts/{account.Id}",
                new { account.Id, account.Name, IsGlobal = false });
        })
        .WithName("CreateChartOfAccount")
        .WithTags("Plan de Cuentas")
        .WithSummary("Agregar una cuenta al plan de cuentas del estudio.")
        .WithDescription("Body: { name: string }. No puede tener el mismo nombre que una cuenta global o ya existente del estudio (case-sensitive). Devuelve 201 con { id, name, isGlobal: false }.")
        .Produces(201)
        .Produces<ProblemDetails>(409);

        // ── DELETE — solo se pueden eliminar cuentas propias del estudio ──────
        app.MapDelete("/api/chart-of-accounts/{id:guid}", async (
            Guid                  id,
            ICurrentTenantService currentTenant,
            ContableAIDbContext   dbContext) =>
        {
            var account = await dbContext.ChartOfAccounts.FindAsync(id);
            if (account == null) return Results.NotFound();

            if (!Guid.TryParse(currentTenant.StudioTenantId, out var studioGuid)
                || account.StudioTenantId != studioGuid)
                return Results.Forbid();

            dbContext.ChartOfAccounts.Remove(account);
            await dbContext.SaveChangesAsync();
            return Results.NoContent();
        })
        .WithName("DeleteChartOfAccount")
        .WithTags("Plan de Cuentas")
        .WithSummary("Eliminar una cuenta del plan de cuentas del estudio.")
        .WithDescription("Solo se pueden eliminar cuentas propias del estudio (StudioTenantId = tenantId del usuario). Las cuentas globales devuelven 403.")
        .Produces(204)
        .Produces<ProblemDetails>(403)
        .Produces<ProblemDetails>(404);
    }
}
