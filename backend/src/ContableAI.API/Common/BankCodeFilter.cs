using ContableAI.Domain.Constants;
using ContableAI.Infrastructure.Persistence;

namespace ContableAI.API.Common;

/// <summary>
/// Filtro por BANCO (Fase C), el nivel de jerarquía que va por encima de la cuenta: un banco tiene
/// varias cuentas de la misma empresa (pesos, dólares, operativa). Es el hermano de
/// <see cref="BankAccountFilter"/> y comparte su contrato con el frontend — mismo sentinel, misma
/// forma de ítem— para que los dos dropdowns se comporten igual en las dos grillas.
///
/// <b>El código de banco NO se desnormaliza</b> en <c>BankTransaction</c> ni en <c>JournalEntry</c>.
/// La razón no es el costo de la migración: <c>Currency</c> y <c>BankAccountId</c> sí están
/// desnormalizados en el asiento, pero ambos son inmutables una vez generado. El banco de una
/// cuenta es un dato editable, y una copia en cada asiento quedaría mintiendo apenas el contador lo
/// corrigiera. El filtro se resuelve entonces por las cuentas de ese banco.
/// </summary>
public static class BankCodeFilter
{
    /// <summary>
    /// Valor de <c>bankCode</c> que selecciona lo que NO se puede atribuir a un banco: los
    /// movimientos y asientos sin cuenta, más los de cuentas a las que todavía no se les cargó el
    /// banco. Los dos casos juntos, para que los buckets del dropdown particionen el total: la suma
    /// de filtrar por cada banco más "sin banco" tiene que dar el universo completo, o el contador
    /// pierde de vista movimientos sin enterarse.
    /// </summary>
    public const string Unassigned = "none";

    /// <summary>Etiqueta del bucket sin banco. La define el backend para que las grillas coincidan.</summary>
    public const string UnassignedLabel = "Sin banco";

    /// <summary>Ítem del dropdown: el código que se manda de vuelta y el nombre que se muestra.</summary>
    public sealed record Item(string Code, string Label);

    /// <summary>
    /// Traduce el parámetro a su forma canónica. Devuelve <c>false</c> ante un valor no reconocido
    /// para que el endpoint responda 400, en vez de ignorar el filtro en silencio y devolver de más
    /// — mismo criterio que <see cref="BankAccountFilter.TryParse"/>.
    /// </summary>
    public static bool TryParse(string? bankCode, out string? code, out bool unassignedOnly)
    {
        code = null;
        unassignedOnly = false;

        if (string.IsNullOrWhiteSpace(bankCode)) return true;

        if (string.Equals(bankCode, Unassigned, StringComparison.OrdinalIgnoreCase))
        {
            unassignedOnly = true;
            return true;
        }

        code = BankCodes.Normalize(bankCode);
        return code is not null;
    }

    /// <summary>
    /// Bancos presentes en el alcance consultado, derivados de las cuentas realmente en uso —no de
    /// todas las del catálogo—, igual que los filtros de mes, año y cuenta: ofrecer un banco sin
    /// datos en el período solo lleva a una pantalla vacía.
    /// </summary>
    public static async Task<List<Item>> BuildAsync(
        ContableAIDbContext db, IReadOnlyCollection<Guid?> usedIds, CancellationToken ct = default)
        => From(await BankAccountFilter.BuildAsync(db, usedIds, ct));

    /// <summary>
    /// Misma lista que <see cref="BuildAsync"/> pero SIN tocar la base, a partir de las cuentas ya
    /// materializadas. Es la vía que usan los endpoints que arman los dos dropdowns en la misma
    /// respuesta: las cuentas ya se consultaron para el filtro de cuenta, y los bancos son una
    /// proyección de ese mismo conjunto.
    /// </summary>
    public static List<Item> From(IEnumerable<BankAccountFilter.Item> accounts)
    {
        var accountList = accounts.ToList();

        var items = accountList
            .Where(a => a.Id != BankAccountFilter.Unassigned && a.BankCode is not null)
            .Select(a => a.BankCode!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(code => new Item(code, BankCodes.DisplayName(code)))
            .OrderBy(b => b.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // El bucket sin banco solo se ofrece si hay algo adentro: cuentas sin banco cargado, o
        // movimientos sin cuenta.
        var hasUnassigned = accountList.Any(a =>
            a.Id == BankAccountFilter.Unassigned || a.BankCode is null);

        if (hasUnassigned)
            items.Insert(0, new Item(Unassigned, UnassignedLabel));

        return items;
    }

    /// <summary>
    /// Cuentas del alcance que pertenecen al banco pedido. Con <paramref name="code"/> en
    /// <c>null</c> devuelve las que no tienen banco cargado.
    ///
    /// Resolver el filtro contra estos IDs, y no con un <c>EXISTS</c> correlacionado contra
    /// <c>BankAccounts</c>, tiene dos ventajas concretas: las cuentas ya están en memoria (cero
    /// queries extra) y el predicado resultante es un <c>IN</c> sobre <c>BankAccountId</c>, que es
    /// justo la columna indexada por <c>IX_JournalEntries_CompanyId_BankAccountId</c> y
    /// <c>IX_BankTransactions_BankAccountId_Date</c>. Una empresa tiene un puñado de cuentas, así
    /// que la lista de parámetros es corta y estable.
    /// </summary>
    public static List<Guid> AccountIdsFor(IEnumerable<BankAccountFilter.Item> accounts, string? code)
        => accounts
            .Where(a => a.Id != BankAccountFilter.Unassigned
                     && string.Equals(a.BankCode, code, StringComparison.OrdinalIgnoreCase))
            .Select(a => Guid.Parse(a.Id))
            .ToList();
}
