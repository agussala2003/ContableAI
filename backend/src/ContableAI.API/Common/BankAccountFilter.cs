using ContableAI.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ContableAI.API.Common;

/// <summary>
/// Piezas compartidas del filtro por cuenta bancaria (F1.e), usadas por la grilla de movimientos y
/// por el libro diario. Viven acá y no duplicadas en cada endpoint porque el sentinel y la forma
/// del ítem son un contrato con el frontend: si divergen, un dropdown filtra distinto que el otro.
/// </summary>
public static class BankAccountFilter
{
    /// <summary>
    /// Valor de <c>bankAccountId</c> que selecciona los movimientos/asientos SIN cuenta asignada.
    /// Existe porque en una query string no hay forma de expresar "el valor nulo": un parámetro
    /// ausente significa "todas", no "las que no tienen".
    /// </summary>
    public const string Unassigned = "none";

    /// <summary>Etiqueta del bucket sin cuenta. La define el backend para que las dos grillas coincidan.</summary>
    public const string UnassignedLabel = "Sin cuenta asignada";

    /// <summary>Ítem del dropdown. <c>Id</c> es un GUID en texto, o <see cref="Unassigned"/>.</summary>
    public sealed record Item(string Id, string Alias, string Currency);

    /// <summary>
    /// Traduce el parámetro a un predicado. Devuelve <c>false</c> si el valor no es interpretable,
    /// para que el endpoint responda 400 en vez de ignorar el filtro en silencio y devolver de más.
    /// </summary>
    public static bool TryParse(string? bankAccountId, out Guid? id, out bool unassignedOnly)
    {
        id = null;
        unassignedOnly = false;

        if (string.IsNullOrWhiteSpace(bankAccountId)) return true;

        if (string.Equals(bankAccountId, Unassigned, StringComparison.OrdinalIgnoreCase))
        {
            unassignedOnly = true;
            return true;
        }

        if (Guid.TryParse(bankAccountId, out var parsed))
        {
            id = parsed;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Arma las opciones del dropdown a partir de las cuentas realmente presentes en el alcance
    /// consultado —no de todas las cuentas de la empresa—, igual que los filtros de mes y año: una
    /// cuenta sin datos en el período solo ofrece una vista vacía.
    /// </summary>
    public static async Task<List<Item>> BuildAsync(
        ContableAIDbContext db, IReadOnlyCollection<Guid?> usedIds, CancellationToken ct = default)
    {
        var ids = usedIds.Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToList();

        var accounts = ids.Count == 0
            ? []
            : await db.BankAccounts.AsNoTracking()
                .Where(a => ids.Contains(a.Id))
                .Select(a => new Item(a.Id.ToString(), a.Alias, a.Currency))
                .ToListAsync(ct);

        var items = accounts.OrderBy(a => a.Alias, StringComparer.OrdinalIgnoreCase).ToList();

        // El bucket sin cuenta va primero: son los movimientos que no pueden asentarse y que el
        // contador tiene que resolver.
        if (usedIds.Any(x => !x.HasValue))
            items.Insert(0, new Item(Unassigned, UnassignedLabel, string.Empty));

        return items;
    }
}
