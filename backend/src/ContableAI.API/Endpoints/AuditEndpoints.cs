using ContableAI.Application.Common;
using ContableAI.Application.Features.Audit.Queries;
using ContableAI.API.Common;
using ContableAI.Infrastructure.Services;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace ContableAI.API.Endpoints;

public static class AuditEndpoints
{
    public static void MapAuditEndpoints(this WebApplication app)
    {
        app.MapGet("/api/audit", async (
            ICurrentTenantService tenant,
            IMediator             mediator,
            string?               entityName,
            string?               action,
            string?               userId,
            int                   page     = 1,
            int                   pageSize = 50) =>
        {
            var query  = new GetAuditLogQuery(tenant.StudioTenantId!, entityName, action, userId, page, pageSize);
            var result = await mediator.Send(query);
            return result.ToHttpResult();
        })
        .RequireAuthorization(p => p.RequireRole("StudioOwner"))
        .WithName("GetAuditLog")
        .WithTags("Auditoría")
        .WithSummary("Log de auditoría paginado del estudio (solo StudioOwner).")
        .WithDescription("Query params: entityName (string), action (Created/Updated/Deleted), userId, page (defecto 1), pageSize (defecto 50, máx 200). Solo StudioOwner puede consultar el log.")
        .Produces<AuditLogResponse>(200);
    }
}
