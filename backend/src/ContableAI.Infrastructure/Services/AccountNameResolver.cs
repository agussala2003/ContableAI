using ContableAI.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ContableAI.Infrastructure.Services;

/// <summary>
/// Resuelve el nombre de una cuenta contable a su forma canónica del plan de cuentas,
/// ignorando diferencias de mayúsculas/minúsculas y espacios sobrantes.
/// Evita que el mismo concepto (ej: "cargas sociales" cargado a mano vs "Cargas Sociales"
/// asignado por el cruce AFIP) termine en dos cuentas distintas.
/// </summary>
public interface IAccountNameResolver
{
    /// <summary>
    /// Construye un mapa de canonicalización una sola vez (para uso en lote sin N+1).
    /// </summary>
    Task<CanonicalAccountMap> BuildMapAsync(Guid? studioGuid, CancellationToken ct = default);

    /// <summary>
    /// Resuelve un único nombre crudo a su forma canónica. Conveniencia para escrituras puntuales.
    /// </summary>
    Task<string> ResolveAsync(string rawName, Guid? studioGuid, CancellationToken ct = default);
}

/// <summary>
/// Diccionario inmutable nombre-normalizado → nombre canónico construido a partir del plan de cuentas.
/// </summary>
public sealed class CanonicalAccountMap
{
    private readonly Dictionary<string, string> _canonicalByName;

    /// <param name="accountNames">
    /// Nombres del plan de cuentas. Deben venir con las cuentas globales primero, de modo que
    /// la casing global gane sobre una variante personalizada del estudio.
    /// </param>
    public CanonicalAccountMap(IEnumerable<string> accountNames)
    {
        _canonicalByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in accountNames)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            var trimmed = name.Trim();
            // TryAdd: la primera coincidencia (global) gana; ignora variantes de casing posteriores.
            _canonicalByName.TryAdd(trimmed, trimmed);
        }
    }

    /// <summary>
    /// Devuelve el nombre canónico si existe una cuenta equivalente (case-insensitive);
    /// si no, devuelve el texto trimmeado tal cual (cuenta libre que aún no está en el plan).
    /// </summary>
    public string Resolve(string? rawName)
    {
        var trimmed = (rawName ?? string.Empty).Trim();
        if (trimmed.Length == 0) return trimmed;
        return _canonicalByName.TryGetValue(trimmed, out var canonical) ? canonical : trimmed;
    }
}

public sealed class AccountNameResolver : IAccountNameResolver
{
    private readonly ContableAIDbContext _db;

    public AccountNameResolver(ContableAIDbContext db) => _db = db;

    public async Task<CanonicalAccountMap> BuildMapAsync(Guid? studioGuid, CancellationToken ct = default)
    {
        var names = await _db.ChartOfAccounts
            .AsNoTracking()
            .Where(a => a.StudioTenantId == null || a.StudioTenantId == studioGuid)
            .OrderBy(a => a.StudioTenantId == null ? 0 : 1) // globales primero
            .Select(a => a.Name)
            .ToListAsync(ct);

        return new CanonicalAccountMap(names);
    }

    public async Task<string> ResolveAsync(string rawName, Guid? studioGuid, CancellationToken ct = default)
    {
        var map = await BuildMapAsync(studioGuid, ct);
        return map.Resolve(rawName);
    }
}
