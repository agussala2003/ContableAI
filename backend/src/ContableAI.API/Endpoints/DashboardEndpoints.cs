using ContableAI.Application.Features.Dashboard.Queries;
using ContableAI.API.Common;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace ContableAI.API.Endpoints;

public static class DashboardEndpoints
{
    public static void MapDashboardEndpoints(this WebApplication app)
    {
        app.MapGet("/api/dashboard/stats", async (
            IMediator mediator,
            Guid      companyId,
            int?      month = null,
            int?      year  = null) =>
        {
            var query  = new GetDashboardStatsQuery(companyId, month, year);
            var result = await mediator.Send(query);
            return result.ToHttpResult();
        })
        .RequireAuthorization()
        .WithName("GetDashboardStats")
        .WithTags("Dashboard")
        .WithSummary("KPIs de conciliación del mes para una empresa.")
        .WithDescription("Retorna TotalTransactions, PendingClassification, Classified y LowConfidence para el mes/año indicado (o el mes actual si se omiten). Query params: companyId (requerido), month, year.")
        .Produces<DashboardStatsResponse>(200)
        .Produces<ProblemDetails>(401);
    }
}
