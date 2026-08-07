using ContableAI.Domain.Common;
using ContableAI.Domain.Constants;

namespace ContableAI.Infrastructure.Features.Rules;

/// <summary>
/// Decide, movimiento por movimiento, si la reaplicación forzada de una regla lo modifica o lo
/// deja intacto, y por qué. Es la parte de la operación que define QUÉ se sobrescribe, así que
/// vive separada de la query y del job para poder testearse sin base de datos.
///
/// La entrada es el conjunto que ya pasó el prefiltro SQL por keyword (patrón ILIKE de
/// <see cref="KeywordMatcher"/>); acá se reconfirma la coincidencia exacta y se aplican las
/// exclusiones que el prefiltro no puede expresar.
/// </summary>
internal static class ReapplyCandidateClassifier
{
    /// <summary>Proyección mínima de un movimiento: lo justo para decidir y para reportar.</summary>
    internal sealed record Candidate(
        Guid    Id,
        string  Description,
        DateOnly Date,
        Guid?   JournalEntryId,
        string  ClassificationSource,
        string? AssignedAccount,
        Guid?   AppliedRuleId);

    internal sealed record Outcome(
        List<Guid> ToUpdate,
        int Pending,
        int ByOtherRule,
        int Manual,
        int SkippedSettled,
        int SkippedClosedPeriod,
        int SkippedAfipCombo,
        int AlreadyApplied);

    /// <summary>
    /// Reglas de exclusión, en orden de precedencia:
    ///   1. No coincide de verdad con el keyword → ni se cuenta (el ILIKE es solo un prefiltro).
    ///   2. Ya tiene asiento generado → intocable: es el "Asentado" del requerimiento.
    ///   3. Viene de un cruce múltiple AFIP → pisarlo dejaría huérfanos los VEPs vinculados y el
    ///      asiento perdería el desglose por impuesto que arma <c>ProjectLines</c>.
    ///   4. Cae en un período contable cerrado → misma regla que la generación y el borrado.
    ///   5. Ya está exactamente como lo dejaría esta regla → no se reescribe, así reaplicar dos
    ///      veces seguidas reporta 0 en la segunda en vez de inflar el número.
    /// El resto se actualiza, contabilizado por su origen previo para que la UI pueda advertir
    /// cuántas asignaciones manuales se van a perder.
    /// </summary>
    internal static Outcome Classify(
        IEnumerable<Candidate> candidates,
        string                 keyword,
        string                 targetAccount,
        string                 targetSource,
        Guid                   ruleId,
        IReadOnlySet<(int Year, int Month)> closedPeriods)
    {
        var toUpdate = new List<Guid>();
        int pending = 0, byOtherRule = 0, manual = 0;
        int skippedSettled = 0, skippedClosedPeriod = 0, skippedAfipCombo = 0, alreadyApplied = 0;

        foreach (var c in candidates)
        {
            if (!KeywordMatcher.Matches(c.Description, keyword))
                continue;

            if (c.JournalEntryId is not null)
            {
                skippedSettled++;
                continue;
            }

            if (c.ClassificationSource == ClassificationSources.AfipComboMatch)
            {
                skippedAfipCombo++;
                continue;
            }

            if (closedPeriods.Contains((c.Date.Year, c.Date.Month)))
            {
                skippedClosedPeriod++;
                continue;
            }

            if (c.AppliedRuleId == ruleId
                && string.Equals(c.AssignedAccount, targetAccount, StringComparison.Ordinal)
                && c.ClassificationSource == targetSource)
            {
                alreadyApplied++;
                continue;
            }

            if (string.IsNullOrWhiteSpace(c.AssignedAccount)
                || c.ClassificationSource == ClassificationSources.Pending)
                pending++;
            else if (c.ClassificationSource == ClassificationSources.Manual)
                manual++;
            else
                byOtherRule++;

            toUpdate.Add(c.Id);
        }

        return new Outcome(
            toUpdate, pending, byOtherRule, manual,
            skippedSettled, skippedClosedPeriod, skippedAfipCombo, alreadyApplied);
    }
}
