using ContableAI.API.Common;
using ContableAI.Application.Features.Admin.Commands;
using ContableAI.Application.Features.Admin.Queries;
using ContableAI.Infrastructure.Services;
using MediatR;
using System.Security.Claims;

namespace ContableAI.API.Endpoints;

public record AdminUpdatePlanRequest(string Plan);
public record AdminUpdateRoleRequest(string Role);
public record AdminUpdateDisplayNameRequest(string DisplayName);
public record AdminGlobalRuleRequest(string Keyword, string TargetAccount, string? Direction, int? Priority, bool? RequiresTaxMatching);

/// <summary>Acreditación manual de un pack de extractos. <c>Reference</c> es la clave de idempotencia.</summary>
public record AdminTopUpQuotaRequest(int Amount, string Reference);

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        app.MapGet("/api/admin/users", async (IMediator mediator, CancellationToken ct) =>
        {
            var result = await mediator.Send(new GetAdminUsersQuery(), ct);
            return result.ToHttpResult();
        })
        .RequireAuthorization(p => p.RequireRole("SystemAdmin"))
        .WithName("AdminGetUsers")
        .WithTags("Administración")
        .WithSummary("Lista todos los usuarios con consumo de cuotas y plan.")
        .WithDescription("Devuelve usuarios ordenados por fecha de creación. Incluye plan, rol, estado de cuenta (0=Pendiente/1=Activo/2=Suspendido), cantidad de empresas activas, transacciones del mes en curso y límites del plan (maxCompanies, maxMonthlyTransactions). Solo SystemAdmin.")
        .Produces(200);

        app.MapPut("/api/admin/users/{id:guid}/activate", async (Guid id, IMediator mediator, CancellationToken ct) =>
        {
            var result = await mediator.Send(new ActivateUserCommand(id), ct);
            return result.ToHttpResult();
        })
        .RequireAuthorization(p => p.RequireRole("SystemAdmin"))
        .WithName("AdminActivateUser")
        .WithTags("Administración")
        .WithSummary("Activar una cuenta de usuario.")
        .WithDescription("Cambia AccountStatus a Active e IsActive = true. No tiene efecto si el usuario ya está activo.")
        .Produces(200)
        .Produces(404);

        app.MapPut("/api/admin/users/{id:guid}/suspend", async (Guid id, IMediator mediator, CancellationToken ct) =>
        {
            var result = await mediator.Send(new SuspendUserCommand(id), ct);
            return result.ToHttpResult();
        })
        .RequireAuthorization(p => p.RequireRole("SystemAdmin"))
        .WithName("AdminSuspendUser")
        .WithTags("Administración")
        .WithSummary("Suspender una cuenta de usuario.")
        .WithDescription("Cambia AccountStatus a Suspended e IsActive = false. No se puede suspender a un SystemAdmin.")
        .Produces(200)
        .Produces(404);

        app.MapPatch("/api/admin/users/{id:guid}/plan", async (Guid id, AdminUpdatePlanRequest req, IMediator mediator, CancellationToken ct) =>
        {
            var result = await mediator.Send(new UpdateUserPlanCommand(id, req.Plan), ct);
            return result.ToHttpResult();
        })
        .RequireAuthorization(p => p.RequireRole("SystemAdmin"))
        .WithName("AdminUpdateUserPlan")
        .WithTags("Administración")
        .WithSummary("Cambiar el plan de suscripción de un usuario.")
        .WithDescription("Body: { plan: \"Free\" | \"Pro\" | \"Enterprise\" }. El cambio es inmediato y afecta los límites de cuota del estudio.")
        .Produces(200)
        .Produces(400)
        .Produces(404);

        app.MapPatch("/api/admin/users/{id:guid}/role", async (Guid id, AdminUpdateRoleRequest req, IMediator mediator, CancellationToken ct) =>
        {
            var result = await mediator.Send(new UpdateUserRoleCommand(id, req.Role), ct);
            return result.ToHttpResult();
        })
        .RequireAuthorization(p => p.RequireRole("SystemAdmin"))
        .WithName("AdminUpdateUserRole")
        .WithTags("Administración")
        .WithSummary("Cambiar el rol de un usuario.")
        .WithDescription("Body: { role: \"StudioOwner\" | \"DataEntry\" }. No se puede cambiar el rol de un SystemAdmin ni asignar ese rol.")
        .Produces(200)
        .Produces(400)
        .Produces(404);

        app.MapPatch("/api/admin/users/{id:guid}/display-name", async (Guid id, AdminUpdateDisplayNameRequest req, IMediator mediator, CancellationToken ct) =>
        {
            var result = await mediator.Send(new UpdateUserDisplayNameCommand(id, req.DisplayName), ct);
            return result.ToHttpResult();
        })
        .RequireAuthorization(p => p.RequireRole("SystemAdmin"))
        .WithName("AdminUpdateUserDisplayName")
        .WithTags("Administración")
        .WithSummary("Actualizar el nombre visible de un usuario.")
        .WithDescription("Body: { displayName: string }. No puede estar vacío. Se aplica trim automáticamente.")
        .Produces(200)
        .Produces(400)
        .Produces(404);

        app.MapDelete("/api/admin/users/{id:guid}", async (Guid id, IMediator mediator, CancellationToken ct) =>
        {
            var result = await mediator.Send(new DeleteUserCommand(id), ct);
            return result.ToHttpResult();
        })
        .RequireAuthorization(p => p.RequireRole("SystemAdmin"))
        .WithName("AdminDeleteUser")
        .WithTags("Administración")
        .WithSummary("Eliminar un usuario y todos los datos de su estudio.")
        .WithDescription("Si es el último usuario del estudio, elimina en cascada: líneas de asiento, asientos, transacciones, reglas, períodos cerrados, empresas y plan de cuentas propio. No se puede eliminar un SystemAdmin. Acción irreversible.")
        .Produces(200)
        .Produces(400)
        .Produces(404);

        app.MapDelete("/api/admin/tenants/{studioTenantId}", async (
            string studioTenantId,
            string? reason,
            ClaimsPrincipal user,
            IMediator mediator,
            CancellationToken ct) =>
        {
            var requestedBy = user.FindFirst(ClaimTypes.Email)?.Value
                           ?? user.FindFirst("email")?.Value
                           ?? "system-admin";
            var result = await mediator.Send(new DeleteStudioTenantCommand(studioTenantId, requestedBy, reason), ct);
            return result.ToHttpResult();
        })
        .RequireAuthorization(p => p.RequireRole("SystemAdmin"))
        .WithName("AdminDeleteStudioTenant")
        .WithTags("Administración", "Privacidad")
        .WithSummary("Cierre formal de cuenta: eliminar un estudio completo (derecho al olvido).")
        .WithDescription("Elimina transaccionalmente TODOS los datos del tenant (usuarios, sesiones, empresas —incluidas las dadas de baja—, transacciones, asientos, reglas de empresa y de estudio, sugerencias, comprobantes AFIP, plan de cuentas propio, períodos cerrados y resultados de jobs) y seudonimiza los AuditLogs (email → deleted-user-{id}@anonymized.local, diffs vaciados) conservándolos por trazabilidad legal. Query param opcional: ?reason= (se persiste en el AuditLog de cierre junto con el email del admin ejecutante). Devuelve el conteo por tabla como evidencia. No aplica a tenants con SystemAdmin. Acción irreversible — Ley 25.326 / GDPR (P-1).")
        .Produces<DeleteStudioTenantResponse>(200)
        .Produces(400)
        .Produces(404);

        app.MapGet("/api/admin/stats", async (IMediator mediator, CancellationToken ct) =>
        {
            var result = await mediator.Send(new GetAdminStatsQuery(), ct);
            return result.ToHttpResult();
        })
        .RequireAuthorization(p => p.RequireRole("SystemAdmin"))
        .WithName("AdminGetStats")
        .WithTags("Administración")
        .WithSummary("Estadísticas globales del sistema.")
        .WithDescription("Devuelve: totalUsers, activeUsers, pendingUsers, suspendedUsers, totalCompanies, totalTransactions, monthlyTransactions (mes actual), totalJournalEntries y planDistribution ([{ plan, count }]). Solo SystemAdmin.")
        .Produces(200);

        app.MapPost("/api/admin/users/{id:guid}/send-password-reset", async (Guid id, IMediator mediator, CancellationToken ct) =>
        {
            var result = await mediator.Send(new SendAdminPasswordResetCommand(id), ct);
            return result.ToHttpResult();
        })
        .RequireAuthorization(p => p.RequireRole("SystemAdmin"))
        .WithName("AdminSendPasswordReset")
        .WithTags("Administración")
        .WithSummary("Enviar email de recuperación de contraseña a un usuario.")
        .WithDescription("Genera un token de reset válido por 24 horas, lo persiste en la BD y envía el correo. Si el envío de email falla, devuelve 500 pero el token queda guardado.")
        .Produces(200)
        .Produces(404)
        .Produces(500);

        app.MapGet("/api/admin/rules", async (IMediator mediator, CancellationToken ct) =>
        {
            var result = await mediator.Send(new GetAdminGlobalRulesQuery(), ct);
            return result.ToHttpResult();
        })
        .RequireAuthorization(p => p.RequireRole("SystemAdmin"))
        .WithName("AdminGetGlobalRules")
        .WithTags("Administración")
        .WithSummary("Listar reglas de clasificación globales.")
        .WithDescription("Devuelve reglas con CompanyId = null, ordenadas por prioridad y keyword. Estas reglas aplican como fallback a todos los estudios cuando no hay regla específica de empresa.")
        .Produces(200);

        app.MapPost("/api/admin/rules", async (AdminGlobalRuleRequest req, IMediator mediator, CancellationToken ct) =>
        {
            var result = await mediator.Send(
                new CreateAdminGlobalRuleCommand(req.Keyword, req.TargetAccount, req.Direction, req.Priority, req.RequiresTaxMatching),
                ct);

            return result.ToCreatedResult(result.Value is null ? null : $"/api/admin/rules/{result.Value.Id}");
        })
        .RequireAuthorization(p => p.RequireRole("SystemAdmin"))
        .WithName("AdminCreateGlobalRule")
        .WithTags("Administración")
        .WithSummary("Crear una regla de clasificación global.")
        .WithDescription("Body: { keyword: string, targetAccount: string, direction: \"DEBIT\" | \"CREDIT\" | null, priority: int (defecto 100), requiresTaxMatching: bool }. El keyword se guarda en mayúsculas. CompanyId = null (aplica globalmente).")
        .Produces(201)
        .Produces(400);

        app.MapPut("/api/admin/rules/{id:guid}", async (Guid id, AdminGlobalRuleRequest req, IMediator mediator, CancellationToken ct) =>
        {
            var result = await mediator.Send(
                new UpdateAdminGlobalRuleCommand(id, req.Keyword, req.TargetAccount, req.Direction, req.Priority, req.RequiresTaxMatching),
                ct);

            return result.ToHttpResult();
        })
        .RequireAuthorization(p => p.RequireRole("SystemAdmin"))
        .WithName("AdminUpdateGlobalRule")
        .WithTags("Administración")
        .WithSummary("Actualizar una regla de clasificación global.")
        .WithDescription("Body: { keyword, targetAccount, direction, priority, requiresTaxMatching }. Solo opera sobre reglas con CompanyId = null.")
        .Produces(200)
        .Produces(400)
        .Produces(404);

        app.MapDelete("/api/admin/rules/{id:guid}", async (Guid id, IMediator mediator, CancellationToken ct) =>
        {
            var result = await mediator.Send(new DeleteAdminGlobalRuleCommand(id), ct);
            return result.ToHttpResult();
        })
        .RequireAuthorization(p => p.RequireRole("SystemAdmin"))
        .WithName("AdminDeleteGlobalRule")
        .WithTags("Administración")
        .WithSummary("Eliminar una regla de clasificación global.")
        .WithDescription("Borra la regla global por ID. Acción irreversible: la regla deja de aplicarse a todos los estudios.")
        .Produces(200)
        .Produces(404);

        app.MapPost("/api/admin/normalize-accounts", async (IMediator mediator, CancellationToken ct) =>
        {
            var result = await mediator.Send(new AdminNormalizeAccountsCommand(), ct);
            return result.ToHttpResult();
        })
        .RequireAuthorization(p => p.RequireRole("SystemAdmin"))
        .WithName("AdminNormalizeAccounts")
        .WithTags("Administración")
        .WithSummary("Normalizar en lote las cuentas de los movimientos a su forma canónica.")
        .WithDescription("Reescribe BankTransactions.AssignedAccount al nombre canónico del plan de cuentas (case-insensitive), limpiando data legacy con casing mixto generada antes de FIX-A. Idempotente. No toca reglas ni asientos históricos. Devuelve { transactionsScanned, transactionsUpdated }. Solo SystemAdmin.")
        .Produces(200);

        app.MapPost("/api/admin/db-reset", async (IMediator mediator, CancellationToken ct) =>
        {
            var result = await mediator.Send(new AdminDbResetCommand(), ct);
            return result.ToHttpResult();
        })
        .RequireAuthorization(p => p.RequireRole("SystemAdmin"))
        .WithName("AdminDbReset")
        .WithTags("Administración")
        .WithSummary("Vaciar la base de datos y re-sembrar datos iniciales (solo Development).")
        .WithDescription("Borra en orden de dependencia FK: líneas de asiento, asientos, transacciones, períodos, auditoría, reglas, usuarios, empresas, plan de cuentas. Luego re-siembra reglas globales y plan de cuentas predeterminado. Retorna 403 en entornos distintos de Development.")
        .Produces(200)
        .Produces(403);

        // ── Saldo prepago de extractos ────────────────────────────────────────
        // No hay pasarela de pagos: el cobro se hace fuera del sistema y el saldo se acredita acá
        // a mano. Es lo que permite validar el modelo comercial sin integrar Stripe/MercadoPago.
        app.MapPost("/api/admin/tenants/{tenantId}/quota/top-up", async (
            string tenantId,
            AdminTopUpQuotaRequest req,
            IUsageService usage,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(tenantId))
                return Results.BadRequest(new { message = "El estudio es obligatorio." });

            if (req.Amount <= 0)
                return Results.BadRequest(new { message = "La cantidad de extractos tiene que ser mayor que cero." });

            // La referencia ES la clave de idempotencia. Sin ella, dos cargas distintas colisionan
            // entre sí y solo la primera entra; con una referencia mal puesta, un mismo pago se
            // podría acreditar dos veces. Se exige explícitamente en vez de generar una al vuelo.
            if (string.IsNullOrWhiteSpace(req.Reference))
                return Results.BadRequest(new
                {
                    message = "La referencia del comprobante es obligatoria: identifica al pago y evita acreditarlo dos veces.",
                });

            var applied = await usage.AddQuotaAsync(tenantId, req.Amount, req.Reference, ct);
            var balance = await usage.GetAvailableQuotaAsync(tenantId, ct);

            // La carga repetida NO es un error: devuelve 200 con applied=false y el saldo real, para
            // que un reintento del admin (o un doble clic) sea seguro y quede claro qué pasó.
            return Results.Ok(new
            {
                Applied = applied,
                Balance = balance,
                Message = applied
                    ? $"Se acreditaron {req.Amount} extractos al estudio {tenantId}. Saldo disponible: {balance}."
                    : $"El comprobante '{req.Reference}' ya estaba acreditado. Saldo disponible sin cambios: {balance}.",
            });
        })
        .RequireAuthorization(p => p.RequireRole("SystemAdmin"))
        .WithName("AdminTopUpStatementQuota")
        .WithTags("Administración")
        .WithSummary("Acreditar un pack de extractos prepago a un estudio.")
        .WithDescription(
            "Body: { amount (int > 0), reference (string) }. La referencia es el comprobante del pago " +
            "y funciona como clave de idempotencia: reintentar la misma carga devuelve applied=false " +
            "sin acreditar de nuevo. El saldo NO vence. Devuelve { applied, balance, message }.")
        .Produces(200)
        .Produces(400)
        .Produces(403);

        // ── Consulta de saldo ─────────────────────────────────────────────────
        app.MapGet("/api/admin/tenants/{tenantId}/quota", async (
            string tenantId,
            IUsageService usage,
            CancellationToken ct) =>
        {
            var balance = await usage.GetAvailableQuotaAsync(tenantId, ct);
            var period  = await usage.GetCurrentPeriodAsync(tenantId, ct);

            return Results.Ok(new
            {
                TenantId              = tenantId,
                Balance               = balance,
                period.PeriodKey,
                period.StatementsProcessed,
            });
        })
        .RequireAuthorization(p => p.RequireRole("SystemAdmin"))
        .WithName("AdminGetStatementQuota")
        .WithTags("Administración")
        .WithSummary("Saldo de extractos y consumo del mes de un estudio.")
        .WithDescription("Devuelve { tenantId, balance, periodKey, statementsProcessed }. Sirve para verificar una carga antes y después de acreditarla.")
        .Produces(200)
        .Produces(403);
    }
}
