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

    /// <summary>
    /// Saldo de extractos disponible: todo lo cargado menos todo lo consumido, SIN corte por
    /// período. El saldo prepago no vence — es la promesa comercial que destraba la objeción del
    /// contador ("¿y si este mes tengo menos trabajo?").
    /// </summary>
    Task<int> GetAvailableQuotaAsync(string studioTenantId, CancellationToken ct = default);

    /// <summary>
    /// Acredita un pack de extractos ya cobrado. Idempotente por <paramref name="reference"/>:
    /// reintentar la misma carga no acredita dos veces.
    /// </summary>
    /// <param name="reference">
    /// Referencia del comprobante de pago. Es la clave de idempotencia, así que tiene que
    /// identificar al PAGO, no al momento de la carga.
    /// </param>
    /// <returns><c>true</c> si se acreditó; <c>false</c> si esa referencia ya estaba acreditada.</returns>
    Task<bool> AddQuotaAsync(
        string studioTenantId, int amount, string reference, CancellationToken ct = default);
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

    public async Task<int> GetAvailableQuotaAsync(string studioTenantId, CancellationToken ct = default)
    {
        // Una sola pasada por el ledger del estudio, agrupando por tipo: dos consultas separadas
        // (una por cargas y otra por consumos) recorrerían dos veces las mismas filas.
        // El cast a int? es obligatorio: SUM sobre un conjunto vacío devuelve NULL en SQL, y sin él
        // la materialización a int no anulable revienta apenas un estudio no tiene movimientos.
        var totals = await _db.UsageEvents
            .AsNoTracking()
            .Where(u => u.StudioTenantId == studioTenantId)
            .GroupBy(u => u.Type)
            .Select(g => new { Type = g.Key, Total = g.Sum(u => (int?)u.Quantity) ?? 0 })
            .ToListAsync(ct);

        var toppedUp  = totals.FirstOrDefault(t => t.Type == UsageEventType.StatementQuotaTopUp)?.Total ?? 0;
        var consumed  = totals.FirstOrDefault(t => t.Type == UsageEventType.StatementProcessed)?.Total  ?? 0;

        // Los consumos se guardan con cantidad POSITIVA y se restan acá; un reverso es un consumo
        // de cantidad negativa, así que devuelve saldo solo por sumar al total consumido en negativo.
        return toppedUp - consumed;
    }

    public async Task<bool> AddQuotaAsync(
        string studioTenantId, int amount, string reference, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(studioTenantId))
            throw new ArgumentException("El estudio es obligatorio.", nameof(studioTenantId));

        // Sin referencia no hay idempotencia: todas las cargas colisionarían entre sí y solo la
        // primera entraría. Un comprobante vacío es un error de operación, no un caso a tolerar.
        if (string.IsNullOrWhiteSpace(reference))
            throw new ArgumentException(
                "La referencia del comprobante es obligatoria: es la clave de idempotencia de la carga.",
                nameof(reference));

        // Una carga negativa restaría saldo sin dejar rastro de que fue un ajuste. Los reversos se
        // hacen registrando un consumo, no una carga en negativo.
        if (amount <= 0)
            throw new ArgumentOutOfRangeException(nameof(amount), amount,
                "La cantidad de extractos a acreditar tiene que ser mayor que cero.");

        var now = DateTime.UtcNow;
        var topUp = new UsageEvent
        {
            StudioTenantId = studioTenantId,
            CompanyId      = null, // el saldo es del estudio, no de una empresa
            Type           = UsageEventType.StatementQuotaTopUp,
            Quantity       = amount,
            OccurredAt     = now,
            PeriodKey      = UsageEvent.PeriodKeyOf(now),
            // La clave es la referencia SOLA, sin la fecha. Con la fecha adentro, reintentar la
            // misma carga al día siguiente —o que el reintento cruce la medianoche UTC— generaría
            // una clave distinta y acreditaría el pack dos veces: justo lo que la idempotencia
            // tiene que impedir.
            IdempotencyKey = $"TOPUP_{reference.Trim()}",
        };

        _db.UsageEvents.Add(topUp);

        try
        {
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "Saldo acreditado: {Amount} extractos al estudio {Tenant} (comprobante {Reference}).",
                amount, studioTenantId, reference);

            return true;
        }
        catch (DbUpdateException ex)
        {
            _db.Entry(topUp).State = EntityState.Detached;

            _logger.LogWarning(ex,
                "Carga de saldo ignorada: el comprobante {Reference} del estudio {Tenant} ya estaba acreditado.",
                reference, studioTenantId);

            return false;
        }
    }
}
