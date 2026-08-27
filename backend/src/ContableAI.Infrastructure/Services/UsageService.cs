using ContableAI.Domain.Entities;
using ContableAI.Domain.Enums;
using ContableAI.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;

namespace ContableAI.Infrastructure.Services;

/// <summary>Consumo acumulado de un estudio en un período de facturación.</summary>
/// <param name="PeriodKey">Período en formato <c>YYYY-MM</c>.</param>
/// <param name="StatementsProcessed">Extractos procesados y facturables en ese período.</param>
public record UsagePeriodSummary(string PeriodKey, int StatementsProcessed);

/// <summary>
/// Registro y lectura del ledger de facturación.
///
/// Es un servicio SEPARADO de <see cref="IQuotaService"/> a propósito, y la separación no es
/// cosmética: <i>Quota</i> responde "¿puede hacerlo?" contando el stock vivo, e <i>Usage</i>
/// responde "¿cuánto consumió?" leyendo hechos inmutables. Fusionarlos llevaría a facturar un
/// número que se achica cuando el cliente borra datos.
/// </summary>
public interface IUsageService
{
    /// <summary>
    /// Registra un extracto procesado. Idempotente por <paramref name="fileHash"/>: llamarla dos
    /// veces con el mismo hash deja un solo evento.
    /// </summary>
    /// <param name="studioTenantId">Estudio al que se le imputa el consumo.</param>
    /// <param name="companyId">Empresa que lo originó, si se conoce.</param>
    /// <param name="fileHash">SHA-256 del contenido del archivo (ver <see cref="ComputeFileHash"/>).</param>
    /// <returns><c>true</c> si se registró un evento nuevo; <c>false</c> si ya estaba registrado.</returns>
    Task<bool> TrackStatementProcessedAsync(
        string studioTenantId, Guid? companyId, string fileHash, CancellationToken ct = default);

    /// <summary>Consumo del período en curso (mes UTC actual) para un estudio.</summary>
    Task<UsagePeriodSummary> GetCurrentPeriodAsync(string studioTenantId, CancellationToken ct = default);
}

public class UsageService : IUsageService
{
    private readonly ContableAIDbContext _db;
    private readonly ILogger<UsageService> _logger;

    public UsageService(ContableAIDbContext db, ILogger<UsageService> logger)
    {
        _db     = db;
        _logger = logger;
    }

    /// <summary>
    /// SHA-256 del contenido, en hexadecimal minúsculo. Es la identidad del archivo: dos subidas
    /// del mismo extracto —aunque cambie el nombre— dan la misma clave y se cobran una sola vez.
    /// </summary>
    public static string ComputeFileHash(byte[] content) =>
        Convert.ToHexStringLower(SHA256.HashData(content));

    public async Task<bool> TrackStatementProcessedAsync(
        string studioTenantId, Guid? companyId, string fileHash, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(studioTenantId) || string.IsNullOrWhiteSpace(fileHash))
            return false;

        var now = DateTime.UtcNow;
        var usageEvent = new UsageEvent
        {
            StudioTenantId = studioTenantId,
            CompanyId      = companyId,
            Type           = UsageEventType.StatementProcessed,
            Quantity       = 1,
            OccurredAt     = now,
            PeriodKey      = UsageEvent.PeriodKeyOf(now),
            IdempotencyKey = fileHash,
        };

        _db.UsageEvents.Add(usageEvent);

        try
        {
            // SaveChanges propio, no el del pipeline de subida. Es lo que impide que un choque del
            // ledger tire abajo la carga: si esto falla, los movimientos ya están commiteados y el
            // usuario igual recibe su extracto procesado. Cobrar es importante; perder el trabajo
            // del contador por un problema de facturación, inaceptable.
            await _db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex)
        {
            // Choque contra UX_UsageEvents_Tenant_Type_IdempotencyKey: el extracto ya estaba
            // registrado. Es el camino ESPERADO al resubir un archivo o al reintentar un job, no
            // una condición de error.
            _db.Entry(usageEvent).State = EntityState.Detached;

            _logger.LogDebug(ex,
                "Extracto ya registrado en el ledger (tenant {Tenant}, hash {Hash}): no se cobra de nuevo.",
                studioTenantId, fileHash);

            return false;
        }
    }

    public async Task<UsagePeriodSummary> GetCurrentPeriodAsync(
        string studioTenantId, CancellationToken ct = default)
    {
        var periodKey = UsageEvent.PeriodKeyOf(DateTime.UtcNow);

        // SumAsync sobre Quantity y no CountAsync sobre las filas: los reversos se registran como
        // eventos de cantidad negativa, y contar filas los sumaría en lugar de restarlos.
        // El período sin eventos devuelve 0, no null (Sum sobre vacío en SQL da NULL).
        var statements = await _db.UsageEvents
            .AsNoTracking()
            .Where(u => u.StudioTenantId == studioTenantId
                     && u.PeriodKey      == periodKey
                     && u.Type           == UsageEventType.StatementProcessed)
            .SumAsync(u => (int?)u.Quantity, ct) ?? 0;

        return new UsagePeriodSummary(periodKey, statements);
    }
}
