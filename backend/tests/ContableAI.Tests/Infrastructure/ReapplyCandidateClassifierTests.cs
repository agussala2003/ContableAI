using ContableAI.Application.Features.Rules.Commands;
using ContableAI.Domain.Constants;
using ContableAI.Infrastructure.Features.Rules;
using FluentAssertions;
using Candidate = ContableAI.Infrastructure.Features.Rules.ReapplyCandidateClassifier.Candidate;

namespace ContableAI.Tests.Infrastructure;

/// <summary>
/// Epic E — Qué sobrescribe y qué NO la reaplicación forzada de una regla.
///
/// Es la parte destructiva de la feature: a diferencia del reapply clásico, pisa la cuenta
/// asignada incluso cuando la puso el contador a mano. Estos tests fijan las cuatro exclusiones
/// que la protegen (asentado, período cerrado, cruce AFIP y el no-op) y el desglose por origen
/// previo, que es lo que la UI usa para advertir cuánto trabajo manual se va a perder.
/// </summary>
public class ReapplyCandidateClassifierTests
{
    private static readonly Guid RuleId = Guid.NewGuid();
    private const string Keyword = "PAGO PROVEEDOR";
    private const string Target  = "PROVEEDORES";

    private static readonly HashSet<(int Year, int Month)> NoClosedPeriods = [];

    private static Candidate Tx(
        string source,
        string? assignedAccount = null,
        Guid? journalEntryId    = null,
        Guid? appliedRuleId     = null,
        string description      = "PAGO PROVEEDOR 12345",
        int year = 2026, int month = 3) =>
        new(Guid.NewGuid(), description, new DateOnly(year, month, 10),
            journalEntryId, source, assignedAccount, appliedRuleId);

    private static ReapplyCandidateClassifier.Outcome Classify(params Candidate[] candidates) =>
        ReapplyCandidateClassifier.Classify(
            candidates, Keyword, Target, ClassificationSources.HardRule, RuleId, NoClosedPeriods);

    private static ReapplyCandidateClassifier.Outcome ClassifyWithScope(
        ReapplyScope scope, params Candidate[] candidates) =>
        ReapplyCandidateClassifier.Classify(
            candidates, Keyword, Target, ClassificationSources.HardRule, RuleId, NoClosedPeriods, scope);

    // ── v1.2: alcance elegible por el usuario ───────────────────────────────────

    /// <summary>
    /// "Solo pendientes" es la opción no destructiva: completa lo que falta y no pisa nada de lo
    /// que el contador o una regla anterior ya resolvieron.
    /// </summary>
    [Fact]
    public void PendingOnlyScope_LeavesAlreadyClassifiedTransactionsUntouched()
    {
        var outcome = ClassifyWithScope(
            ReapplyScope.PendingOnly,
            Tx(ClassificationSources.Pending),
            Tx(ClassificationSources.Manual,   assignedAccount: "OTRA CUENTA"),
            Tx(ClassificationSources.HardRule, assignedAccount: "OTRA CUENTA", appliedRuleId: Guid.NewGuid()));

        outcome.ToUpdate.Should().HaveCount(1, "solo el movimiento sin cuenta entra en el alcance");
        outcome.Pending.Should().Be(1);
        outcome.Manual.Should().Be(0);
        outcome.ByOtherRule.Should().Be(0);
        outcome.SkippedOutOfScope.Should().Be(2,
            "el usuario necesita ver cuánto más alcanzaría eligiendo Reemplazar");
    }

    /// <summary>
    /// Una cuenta vacía, nula o con el centinela "Pending" son la misma cosa para el usuario: el
    /// movimiento no está categorizado. Las tres tienen que entrar en el alcance "solo pendientes",
    /// porque cuál de las tres quedó grabada depende de por dónde entró el movimiento.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Pending")]
    public void PendingOnlyScope_TreatsEveryFlavourOfUnassignedAsPending(string? assignedAccount)
    {
        var outcome = ClassifyWithScope(
            ReapplyScope.PendingOnly,
            Tx(ClassificationSources.HardRule, assignedAccount: assignedAccount));

        outcome.ToUpdate.Should().HaveCount(1);
        outcome.Pending.Should().Be(1);
        outcome.SkippedOutOfScope.Should().Be(0);
    }

    /// <summary>
    /// El alcance no puede aflojar las exclusiones de integridad: un movimiento asentado sigue
    /// intocable con cualquiera de las dos opciones.
    /// </summary>
    [Theory]
    [InlineData(ReapplyScope.PendingOnly)]
    [InlineData(ReapplyScope.PendingAndUnsettled)]
    public void Scope_NeverOverridesTheSettledExclusion(ReapplyScope scope)
    {
        var outcome = ClassifyWithScope(
            scope,
            Tx(ClassificationSources.Pending, journalEntryId: Guid.NewGuid()));

        outcome.ToUpdate.Should().BeEmpty();
        outcome.SkippedSettled.Should().Be(1);
    }

    /// <summary>El default sigue siendo el comportamiento previo: sin alcance explícito, reemplaza.</summary>
    [Fact]
    public void DefaultScope_StillReplacesEverythingUnsettled()
    {
        var outcome = Classify(
            Tx(ClassificationSources.Pending),
            Tx(ClassificationSources.Manual, assignedAccount: "OTRA CUENTA"));

        outcome.ToUpdate.Should().HaveCount(2);
        outcome.SkippedOutOfScope.Should().Be(0);
    }

    [Fact]
    public void OverwritesEveryUnsettledMatch_RegardlessOfHowItWasClassified()
    {
        // El cambio de comportamiento del requerimiento: antes solo alcanzaba a los "Pending".
        var outcome = Classify(
            Tx(ClassificationSources.Pending),
            Tx(ClassificationSources.Manual,   assignedAccount: "OTRA CUENTA"),
            Tx(ClassificationSources.HardRule, assignedAccount: "OTRA CUENTA", appliedRuleId: Guid.NewGuid()));

        outcome.ToUpdate.Should().HaveCount(3);
        outcome.Pending.Should().Be(1);
        outcome.Manual.Should().Be(1);
        outcome.ByOtherRule.Should().Be(1);
    }

    [Fact]
    public void NeverTouchesSettledTransactions()
    {
        // La única excepción que pidió el negocio: un movimiento con asiento generado es intocable.
        var outcome = Classify(
            Tx(ClassificationSources.Manual, assignedAccount: "OTRA", journalEntryId: Guid.NewGuid()));

        outcome.ToUpdate.Should().BeEmpty();
        outcome.SkippedSettled.Should().Be(1);
        outcome.Manual.Should().Be(0, "un movimiento asentado no cuenta como sobrescritura pendiente");
    }

    [Fact]
    public void NeverTouchesAfipComboMatches()
    {
        // Pisar el origen dejaría huérfanos los VEPs vinculados y el asiento perdería el
        // desglose por impuesto que arma ProjectLines.
        var outcome = Classify(Tx(ClassificationSources.AfipComboMatch, assignedAccount: "AFIP A DETERMINAR"));

        outcome.ToUpdate.Should().BeEmpty();
        outcome.SkippedAfipCombo.Should().Be(1);
    }

    [Fact]
    public void NeverTouchesClosedPeriods()
    {
        var closed = new HashSet<(int Year, int Month)> { (2026, 1) };

        var outcome = ReapplyCandidateClassifier.Classify(
            [
                Tx(ClassificationSources.Pending, year: 2026, month: 1),  // cerrado
                Tx(ClassificationSources.Pending, year: 2026, month: 2),  // abierto
            ],
            Keyword, Target, ClassificationSources.HardRule, RuleId, closed);

        outcome.ToUpdate.Should().ContainSingle();
        outcome.SkippedClosedPeriod.Should().Be(1);
    }

    [Fact]
    public void SkipsRowsAlreadyInTheTargetState()
    {
        // Reaplicar dos veces seguidas debe reportar 0 en la segunda, no volver a "actualizar"
        // filas idénticas e inflar el número que ve el usuario.
        var outcome = Classify(
            Tx(ClassificationSources.HardRule, assignedAccount: Target, appliedRuleId: RuleId));

        outcome.ToUpdate.Should().BeEmpty();
        outcome.AlreadyApplied.Should().Be(1);
    }

    [Fact]
    public void SameAccountFromADifferentRule_IsStillRewritten()
    {
        // Misma cuenta pero aplicada por otra regla: se reescribe para que AppliedRuleId quede
        // apuntando a la regla correcta y la trazabilidad no mienta.
        var outcome = Classify(
            Tx(ClassificationSources.HardRule, assignedAccount: Target, appliedRuleId: Guid.NewGuid()));

        outcome.ToUpdate.Should().ContainSingle();
        outcome.AlreadyApplied.Should().Be(0);
        outcome.ByOtherRule.Should().Be(1);
    }

    [Fact]
    public void RechecksTheKeyword_BecauseTheSqlPassIsOnlyAPrefilter()
    {
        // El ILIKE del prefiltro resuelve mayúsculas según la collation de la base; la decisión
        // final la toma KeywordMatcher, el mismo criterio que usa el motor de clasificación.
        var outcome = Classify(
            Tx(ClassificationSources.Pending, description: "PAGO PROVEEDOR MENSUAL"),
            Tx(ClassificationSources.Pending, description: "PROVEEDOR PAGO INVERTIDO"));

        outcome.ToUpdate.Should().ContainSingle("las palabras del keyword tienen que aparecer en orden");
    }

    [Fact]
    public void EmptyAssignedAccount_CountsAsPending()
    {
        var outcome = Classify(
            Tx(ClassificationSources.HardRule, assignedAccount: null),
            Tx(ClassificationSources.HardRule, assignedAccount: "   "));

        outcome.Pending.Should().Be(2);
        outcome.ByOtherRule.Should().Be(0);
    }

    [Fact]
    public void ExclusionsTakePrecedenceOverEachOther_AndAreNeverDoubleCounted()
    {
        // Un movimiento asentado Y de cruce AFIP se cuenta una sola vez, en la primera exclusión.
        var outcome = Classify(
            Tx(ClassificationSources.AfipComboMatch, assignedAccount: "X", journalEntryId: Guid.NewGuid()));

        outcome.SkippedSettled.Should().Be(1);
        outcome.SkippedAfipCombo.Should().Be(0);
        outcome.ToUpdate.Should().BeEmpty();
    }
}
