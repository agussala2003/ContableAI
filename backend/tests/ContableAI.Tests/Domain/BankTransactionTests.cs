using ContableAI.Domain.Entities;
using ContableAI.Domain.Enums;
using FluentAssertions;

namespace ContableAI.Tests.Domain;

/// <summary>
/// Tests de la entidad BankTransaction.
/// Verifican que el comportamiento del dominio no se rompa con cambios futuros.
/// </summary>
public class BankTransactionTests
{
    private static BankTransaction CreateTx(decimal amount = 1000m, TransactionType type = TransactionType.Debit) =>
        new BankTransaction
        {
            Date        = new DateOnly(2025, 1, 15),
            Description = "TEST TX",
            Amount      = amount,
            Type        = type,
        };

    // ── Assign ──────────────────────────────────────────────────────────────

    [Fact]
    public void Assign_SetsAccountAndSource()
    {
        var tx = CreateTx();
        tx.Assign("Combustibles", null, false, "HardRule");

        tx.AssignedAccount.Should().Be("Combustibles");
        tx.ClassificationSource.Should().Be("HardRule");
        tx.NeedsTaxMatching.Should().BeFalse();
        tx.ConfidenceScore.Should().Be(1.0f);
    }

    [Fact]
    public void Assign_WithTaxMatching_SetsFlag()
    {
        var tx = CreateTx();
        tx.Assign("AFIP a Determinar", null, needsTaxMatching: true, "HardRule");

        tx.NeedsTaxMatching.Should().BeTrue();
        tx.AssignedAccount.Should().Be("AFIP a Determinar");
    }

    [Fact]
    public void Assign_ClampsConfidenceScore_ToZeroOne()
    {
        var tx = CreateTx();
        tx.Assign("Test", confidenceScore: 2.5f);
        tx.ConfidenceScore.Should().Be(1.0f);

        tx.Assign("Test", confidenceScore: -1f);
        tx.ConfidenceScore.Should().Be(0.0f);
    }

    [Fact]
    public void Assign_CanOverwritePreviousClassification()
    {
        var tx = CreateTx();
        tx.Assign("Impuestos Nacionales AFIP", needsTaxMatching: true, source: "HardRule");
        tx.Assign("IVA Compras", source: "AFIP Match");

        tx.AssignedAccount.Should().Be("IVA Compras");
        tx.NeedsTaxMatching.Should().BeFalse();
        tx.ClassificationSource.Should().Be("AFIP Match");
    }

    // ── MarkNeedsBreakdown ───────────────────────────────────────────────────

    [Fact]
    public void MarkNeedsBreakdown_SetsFlag()
    {
        var tx = CreateTx();
        tx.MarkNeedsBreakdown();
        tx.NeedsBreakdown.Should().BeTrue();
    }

    // ── MarkPossibleDuplicate ────────────────────────────────────────────────

    [Fact]
    public void MarkPossibleDuplicate_SetsFlag()
    {
        var tx = CreateTx();
        tx.MarkPossibleDuplicate();
        tx.IsPossibleDuplicate.Should().BeTrue();
    }

    // ── Notes ────────────────────────────────────────────────────────────────

    [Fact]
    public void Notes_DefaultIsNull()
    {
        var tx = CreateTx();
        tx.Notes.Should().BeNull();
    }

    [Fact]
    public void Notes_CanBeSet()
    {
        var tx = CreateTx();
        tx.Notes = "F.931 – Período 202501";
        tx.Notes.Should().Be("F.931 – Período 202501");
    }

}
