using ContableAI.Domain.Entities;
using ContableAI.Domain.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Security.Claims;
using System.Text.Json;

namespace ContableAI.Infrastructure.Persistence;

/// <summary>
/// Interceptor de EF Core que genera entradas de auditoría silenciosamente
/// cada vez que se crea, edita o elimina un BankTransaction, AccountingRule o JournalEntry.
/// </summary>
public class AuditInterceptor : SaveChangesInterceptor
{
    private readonly IHttpContextAccessor _accessor;

    private static readonly HashSet<Type> AuditedTypes =
    [
        typeof(BankTransaction),
        typeof(AccountingRule),
        typeof(JournalEntry),
    ];

    public AuditInterceptor(IHttpContextAccessor accessor)
    {
        _accessor = accessor;
    }

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        AddAuditLogs(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        AddAuditLogs(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void AddAuditLogs(DbContext? context)
    {
        if (context is null) return;

        var user      = _accessor.HttpContext?.User;
        var userId    = user?.FindFirst(ClaimTypes.NameIdentifier)?.Value
                     ?? user?.FindFirst("sub")?.Value
                     ?? "system";
        var email     = user?.FindFirst(ClaimTypes.Email)?.Value
                     ?? user?.FindFirst("email")?.Value
                     ?? "system";
        var tenantId  = user?.FindFirst("studioTenantId")?.Value ?? string.Empty;

        var logs = new List<AuditLog>();

        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (!AuditedTypes.Contains(entry.Entity.GetType())) continue;

            var action = entry.State switch
            {
                EntityState.Added    => AuditAction.Created.ToString(),
                EntityState.Modified => AuditAction.Updated.ToString(),
                EntityState.Deleted  => AuditAction.Deleted.ToString(),
                _                    => (string?)null,
            };

            if (action is null) continue;

            // Obtener el ID de la entidad (todas tienen una prop 'Id' de tipo Guid)
            var entityId = entry.Property("Id").CurrentValue?.ToString() ?? string.Empty;

            // Capturar snapshot de propiedades según el tipo de acción
            string? changes = null;
            if (action == AuditAction.Created.ToString())
            {
                var props = entry.Properties
                    .ToDictionary(p => p.Metadata.Name, p => p.CurrentValue);
                changes = JsonSerializer.Serialize(props);
            }
            else if (action == AuditAction.Updated.ToString())
            {
                var modified = entry.Properties
                    .Where(p => p.IsModified)
                    .ToDictionary(
                        p => p.Metadata.Name,
                        p => new { From = p.OriginalValue, To = p.CurrentValue });

                if (modified.Count > 0)
                    changes = JsonSerializer.Serialize(modified);
            }
            else if (action == AuditAction.Deleted.ToString())
            {
                var props = entry.Properties
                    .ToDictionary(p => p.Metadata.Name, p => p.OriginalValue);
                changes = JsonSerializer.Serialize(props);
            }

            logs.Add(new AuditLog
            {
                TenantId   = tenantId,
                UserId     = userId,
                UserEmail  = email,
                Action     = action,
                EntityName = entry.Entity.GetType().Name,
                EntityId   = entityId,
                Changes    = changes,
                Timestamp  = DateTime.UtcNow,
            });
        }

        if (logs.Count > 0)
            context.Set<AuditLog>().AddRange(logs);
    }
}
