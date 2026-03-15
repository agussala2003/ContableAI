using ContableAI.API.Common;
using ContableAI.Application.Features.Admin.Commands;
using ContableAI.Application.Features.Admin.Queries;
using MediatR;

namespace ContableAI.API.Endpoints;

public record AdminUpdatePlanRequest(string Plan);
public record AdminUpdateRoleRequest(string Role);
public record AdminUpdateDisplayNameRequest(string DisplayName);
public record AdminGlobalRuleRequest(string Keyword, string TargetAccount, string? Direction, int? Priority, bool? RequiresTaxMatching);

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
    }
}
