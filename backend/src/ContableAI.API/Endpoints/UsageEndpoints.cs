using ContableAI.Infrastructure.Services;

namespace ContableAI.API.Endpoints;

/// <summary>
/// Lectura del ledger de consumo (Fase D).
///
/// En esta fase el sistema SOLO MIDE: no hay bloqueo por consumo en ningún endpoint. La decisión
/// es deliberada — cortarle la subida a un contador el 10 del mes, con vencimientos encima, es la
/// forma más rápida de perder al cliente. Primero se juntan semanas de datos reales, después se
/// avisa, y recién al final se bloquea.
/// </summary>
public static class UsageEndpoints
{
    public static void MapUsageEndpoints(this WebApplication app)
    {
        app.MapGet("/api/usage/current", async (
            ICurrentTenantService tenant,
            IUsageService         usage,
            CancellationToken     ct) =>
        {
            var studioTenantId = tenant.StudioTenantId;
            if (string.IsNullOrWhiteSpace(studioTenantId))
                return Results.Unauthorized();

            var summary = await usage.GetCurrentPeriodAsync(studioTenantId, ct);
            var balance = await usage.GetAvailableQuotaAsync(studioTenantId, ct);

            return Results.Ok(new
            {
                summary.PeriodKey,
                summary.StatementsProcessed,
                // El saldo va acá y no en un endpoint aparte porque el usuario los lee juntos:
                // "consumí 12 este mes, me quedan 38". Enterarse del saldo recién cuando la carga
                // se bloquea es la peor forma posible de comunicarlo.
                Balance = balance,
            });
        })
        .RequireAuthorization()
        .WithName("GetCurrentUsage")
        .WithTags("Consumo")
        .WithSummary("Consumo del período en curso para el estudio autenticado.")
        .WithDescription(
            "Devuelve { periodKey, statementsProcessed, balance }: los extractos procesados y facturables " +
            "del mes UTC en curso. Suma la cantidad de los eventos del ledger, no cuenta filas, " +
            "para que un reverso reste en lugar de sumar. `balance` es el saldo prepago disponible " +
            "(cargas menos consumos, sin vencimiento). El bloqueo por falta de saldo lo aplica el " +
            "pipeline de subida, no este endpoint.")
        .Produces(200)
        .Produces(401);
    }
}
