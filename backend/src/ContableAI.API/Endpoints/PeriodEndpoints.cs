using ContableAI.Infrastructure.Persistence;
using ContableAI.Infrastructure.Services;
using ContableAI.Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ContableAI.API.Endpoints;

public static class PeriodEndpoints
{
    public static void MapPeriodEndpoints(this WebApplication app)
    {
        app.MapGet("/api/periods/closed", async (
            ICurrentTenantService currentTenant,
            ContableAIDbContext   db) =>
        {
            var periods = await db.ClosedPeriods
                .AsNoTracking()
                .Where(p => p.StudioTenantId == currentTenant.StudioTenantId)
                .OrderByDescending(p => p.Year).ThenByDescending(p => p.Month)
                .Select(p => new
                {
                    p.Id, p.Year, p.Month,
                    p.ClosedAt, p.ClosedByEmail,
                })
                .ToListAsync();

            return Results.Ok(periods);
        })
        .WithName("GetClosedPeriods")
        .WithTags("Períodos")
        .WithSummary("Listar todos los períodos contables cerrados del estudio.")
        .WithDescription("Devuelve id, year, month, closedAt y closedByEmail. Ordenados de más reciente a más antiguo.")
        .Produces(200);

        app.MapPost("/api/periods/close", async (
            ClosePeriodRequest    req,
            ICurrentTenantService currentTenant,
            ContableAIDbContext   db,
            HttpContext           httpContext) =>
        {
            if (req.Year < 2000 || req.Year > 2100 || req.Month < 1 || req.Month > 12)
                return Results.BadRequest("Año o mes inválido.");

            var alreadyClosed = await db.ClosedPeriods.AnyAsync(p =>
                p.StudioTenantId == currentTenant.StudioTenantId &&
                p.Year  == req.Year &&
                p.Month == req.Month);

            if (alreadyClosed)
                return Results.Conflict($"El período {req.Month:D2}/{req.Year} ya está cerrado.");

            var userEmail = httpContext.User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
                         ?? httpContext.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                         ?? "desconocido";

            db.ClosedPeriods.Add(new ContableAI.Domain.Entities.ClosedPeriod
            {
                StudioTenantId = currentTenant.StudioTenantId!,
                Year           = req.Year,
                Month          = req.Month,
                ClosedByEmail  = userEmail,
            });

            await db.SaveChangesAsync();

            return Results.Ok(new
            {
                message = $"Período {req.Month:D2}/{req.Year} cerrado exitosamente.",
                year    = req.Year,
                month   = req.Month,
            });
        })
        .WithName("ClosePeriod")
        .WithTags("Períodos")
        .WithSummary("Cerrar un período contable.")
        .WithDescription("Body: { year: int, month: int }. Bloquea toda modificación de transacciones y asientos de ese mes/año. Se puede reabrir con DELETE /api/periods/{year}/{month}.")
        .RequireAuthorization(p => p.RequireRole(UserRole.StudioOwner.ToString(), UserRole.SystemAdmin.ToString()))
        .Produces(200)
        .Produces(400)
        .Produces(409);

        app.MapDelete("/api/periods/{year:int}/{month:int}", async (
            int                   year,
            int                   month,
            ICurrentTenantService currentTenant,
            ContableAIDbContext   db) =>
        {
            var period = await db.ClosedPeriods.FirstOrDefaultAsync(p =>
                p.StudioTenantId == currentTenant.StudioTenantId &&
                p.Year  == year &&
                p.Month == month);

            if (period is null)
                return Results.NotFound($"El período {month:D2}/{year} no está cerrado.");

            db.ClosedPeriods.Remove(period);
            await db.SaveChangesAsync();

            return Results.Ok(new { message = $"Período {month:D2}/{year} reabierto exitosamente." });
        })
        .WithName("ReopenPeriod")
        .WithTags("Períodos")
        .WithSummary("Reabrir un período contable cerrado.")
        .WithDescription("Path params: year (int), month (int). Elimina el registro de período cerrado permitiendo nuevamente crear y modificar transacciones y asientos.")
        .RequireAuthorization(p => p.RequireRole(UserRole.StudioOwner.ToString(), UserRole.SystemAdmin.ToString()))
        .Produces(200)
        .Produces(404);
    }

    /// <summary>Verifica si un período está cerrado para el estudio actual.</summary>
    public static async Task<bool> IsPeriodClosedAsync(
        ContableAIDbContext db, string studioTenantId, int year, int month) =>
        await db.ClosedPeriods.AnyAsync(p =>
            p.StudioTenantId == studioTenantId &&
            p.Year  == year &&
            p.Month == month);
}
