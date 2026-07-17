using ContableAI.Domain.Entities;
using ContableAI.Domain.Enums;
using ContableAI.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ContableAI.Infrastructure.Features.Afip;

public sealed record AfipComboVoucherDto(Guid Id, DateOnly Date, decimal Amount, string TaxName);

/// <summary>Una combinación de VEPs cuya sumatoria coincide exactamente con el movimiento.</summary>
public sealed record AfipComboAlternative(IReadOnlyList<AfipComboVoucherDto> Vouchers);

public sealed record AfipComboSuggestion(
    Guid TransactionId,
    DateOnly Date,
    string Description,
    decimal Amount,
    IReadOnlyList<AfipComboAlternative> Alternatives);

/// <summary>
/// Sugiere combinaciones de VEPs pendientes cuya sumatoria coincide EXACTAMENTE con el importe
/// de un débito bancario a AFIP sin conciliar. Caso real: ARCA agrupa dos o más VEPs (ej. IVA +
/// Ingresos Brutos) en un único pago, así que el extracto trae una sola transferencia mientras
/// que el contador descarga un comprobante por cada VEP.
///
/// Este servicio SOLO calcula sugerencias: la conciliación se asienta únicamente cuando el
/// usuario la confirma (puede darse la casualidad de que una sumatoria coincida sin que los
/// comprobantes correspondan al pago). El cruce 1:1 exacto sigue siendo automático vía
/// <see cref="AfipMatchingJob"/>.
/// </summary>
public class AfipCombinationService
{
    private readonly ContableAIDbContext _db;

    /// <summary>Máximo de VEPs por combinación (el caso típico son 2; se admite margen).</summary>
    public const int MaxVouchersPerCombination = 5;

    /// <summary>Máximo de combinaciones alternativas a sugerir por movimiento.</summary>
    public const int MaxAlternativesPerTransaction = 5;

    /// <summary>Ventana de días entre la fecha del movimiento y la de los VEPs (igual al cruce 1:1).</summary>
    public const int MatchWindowDays = 2;

    public AfipCombinationService(ContableAIDbContext db) => _db = db;

    public async Task<List<AfipComboSuggestion>> ComputeSuggestionsAsync(Guid companyId, CancellationToken ct = default)
    {
        var pendingVouchers = await _db.AfipVouchers
            .AsNoTracking()
            .Where(v => v.CompanyId == companyId && !v.IsMatched)
            .ToListAsync(ct);

        if (pendingVouchers.Count < 2) return [];

        // Mismo universo que AfipMatchingJob: movimientos marcados para cruce impositivo.
        // Solo débitos sin asentar — un pago a AFIP es siempre una salida.
        var pendingTxs = await _db.BankTransactions
            .AsNoTracking()
            .Where(t => t.CompanyId == companyId
                     && t.NeedsTaxMatching
                     && t.Type == TransactionType.Debit
                     && t.JournalEntryId == null)
            .OrderBy(t => t.Date)
            .ToListAsync(ct);

        var suggestions = new List<AfipComboSuggestion>();

        foreach (var tx in pendingTxs)
        {
            var candidates = pendingVouchers
                .Where(v => Math.Abs(tx.Date.DayNumber - v.Date.DayNumber) <= MatchWindowDays)
                .ToList();

            if (candidates.Count < 2) continue;

            // Pasada 1 — VEPs con la misma fecha de pago: los VEPs agrupados por ARCA se
            // debitan juntos (mismo día, misma hora), así que es la señal más confiable.
            var alternatives = new List<AfipComboAlternative>();
            var seenSignatures = new HashSet<string>(StringComparer.Ordinal);

            foreach (var dateGroup in candidates.GroupBy(v => v.Date).OrderBy(g => g.Key))
            {
                foreach (var combo in FindExactCombinations(
                    dateGroup.ToList(), tx.Amount,
                    MaxVouchersPerCombination, MaxAlternativesPerTransaction - alternatives.Count))
                {
                    AddIfDistinct(alternatives, seenSignatures, combo);
                }
                if (alternatives.Count >= MaxAlternativesPerTransaction) break;
            }

            // Pasada 2 — solo si no hubo resultados: combinaciones mezclando fechas de la ventana.
            if (alternatives.Count == 0 && candidates.Select(v => v.Date).Distinct().Count() > 1)
            {
                foreach (var combo in FindExactCombinations(
                    candidates, tx.Amount, MaxVouchersPerCombination, MaxAlternativesPerTransaction))
                {
                    AddIfDistinct(alternatives, seenSignatures, combo);
                }
            }

            if (alternatives.Count > 0)
            {
                suggestions.Add(new AfipComboSuggestion(
                    tx.Id, tx.Date, tx.Description, tx.Amount, alternatives));
            }
        }

        return suggestions;
    }

    /// <summary>
    /// Evita repetir alternativas visualmente idénticas: dos VEPs distintos con el mismo
    /// impuesto e importe generan combinaciones equivalentes para el usuario (aplicar
    /// cualquiera de las dos concilia lo mismo).
    /// </summary>
    private static void AddIfDistinct(
        List<AfipComboAlternative> alternatives,
        HashSet<string> seenSignatures,
        IReadOnlyList<AfipVoucher> combo)
    {
        var signature = string.Join(";", combo
            .Select(v => $"{v.TaxName}|{v.Amount:0.00}|{v.Date:yyyy-MM-dd}")
            .OrderBy(s => s, StringComparer.Ordinal));

        if (seenSignatures.Add(signature))
            alternatives.Add(ToAlternative(combo));
    }

    private static AfipComboAlternative ToAlternative(IReadOnlyList<AfipVoucher> combo) =>
        new(combo
            .OrderByDescending(v => v.Amount)
            .Select(v => new AfipComboVoucherDto(v.Id, v.Date, v.Amount, v.TaxName))
            .ToList());

    /// <summary>
    /// Subconjuntos de 2..maxSize vouchers cuya suma es EXACTAMENTE igual a target (decimal,
    /// sin tolerancias). Backtracking sobre candidatos ordenados descendente con doble poda:
    /// corta cuando el resto no alcanza (suma de los mayores disponibles &lt; restante) y cuando
    /// un candidato excede el restante. Los importes iguales al target se excluyen: ese es el
    /// dominio del cruce 1:1 automático.
    /// </summary>
    public static List<List<AfipVoucher>> FindExactCombinations(
        IReadOnlyList<AfipVoucher> candidates,
        decimal target,
        int maxSize = MaxVouchersPerCombination,
        int maxResults = MaxAlternativesPerTransaction)
    {
        var results = new List<List<AfipVoucher>>();
        if (maxResults <= 0 || target <= 0) return results;

        var sorted = candidates
            .Where(v => v.Amount > 0 && v.Amount < target)
            .OrderByDescending(v => v.Amount)
            .ToList();

        if (sorted.Count < 2) return results;

        // prefix[i] = suma de los primeros i importes (los mayores, por el orden descendente)
        var prefix = new decimal[sorted.Count + 1];
        for (int i = 0; i < sorted.Count; i++)
            prefix[i + 1] = prefix[i] + sorted[i].Amount;

        var current = new List<AfipVoucher>(maxSize);

        void Dfs(int start, decimal remaining)
        {
            if (results.Count >= maxResults) return;
            if (remaining == 0)
            {
                if (current.Count >= 2) results.Add([.. current]);
                return;
            }
            if (current.Count >= maxSize) return;

            for (int i = start; i < sorted.Count; i++)
            {
                if (results.Count >= maxResults) return;

                // Ni tomando los mayores importes disponibles se llega al restante → cortar rama
                int slots = maxSize - current.Count;
                var maxReachable = prefix[Math.Min(sorted.Count, i + slots)] - prefix[i];
                if (maxReachable < remaining) break;

                if (sorted[i].Amount > remaining) continue;

                current.Add(sorted[i]);
                Dfs(i + 1, remaining - sorted[i].Amount);
                current.RemoveAt(current.Count - 1);
            }
        }

        Dfs(0, target);
        return results;
    }
}
