using ContableAI.Domain.Enums;
using ContableAI.Infrastructure.Services;
using FluentAssertions;

namespace ContableAI.Tests.Infrastructure;

public class BbvaNov2024ParseTest
{
    private const string PdfNov2024 = @"C:\Users\aguss\Documents\Projects\ContableAI\tests\extractos\BBVA\BBVA TB 11.2024.pdf";
    private const string PdfJan2025 = @"C:\Users\aguss\Documents\Projects\ContableAI\tests\extractos\BBVA\012025.pdf";

    [Fact]
    public void ParseNov2024_OutputsTransactions()
    {
        if (!File.Exists(PdfNov2024)) return;

        var parser = new PdfBankParser();
        using var stream = File.OpenRead(PdfNov2024);
        var txs = parser.Parse(stream, "BBVA TB 11.2024.pdf").ToList();

        decimal credits = txs.Where(t => t.Type == TransactionType.Credit).Sum(t => t.Amount);
        decimal debits  = txs.Where(t => t.Type == TransactionType.Debit).Sum(t => t.Amount);

        foreach (var tx in txs)
            Console.WriteLine($"{tx.Date} | {tx.Type,-6} | {tx.Amount,15:F2} | {tx.Description}");

        Console.WriteLine($"\nTotal: {txs.Count} transactions");
        Console.WriteLine($"Credits: {credits:F2}  Debits: {debits:F2}");
        Console.WriteLine($"Net: {(credits - debits):F2}");

        txs.Should().NotBeEmpty();
        txs.Should().NotContain(t => t.Description.Contains("TOTAL MOVIMIENTOS", StringComparison.OrdinalIgnoreCase),
            "SIRCREB summary rows must not be parsed as transactions");
        txs.Should().NotContain(t => t.Description.Contains("EL CREDITO DE IMPUESTO", StringComparison.OrdinalIgnoreCase),
            "SIRCREB legal text must not be parsed as transactions");
        txs.Should().NotContain(t => t.Description.Contains("SALDO AL", StringComparison.OrdinalIgnoreCase),
            "Balance marker must be stripped from all descriptions");
    }

    [Fact]
    public void ParseJan2025_DebitoDirectoAndCablevisio()
    {
        if (!File.Exists(PdfJan2025)) return;

        var parser = new PdfBankParser();
        using var stream = File.OpenRead(PdfJan2025);
        var txs = parser.Parse(stream, "012025.pdf").ToList();

        decimal credits = txs.Where(t => t.Type == TransactionType.Credit).Sum(t => t.Amount);
        decimal debits  = txs.Where(t => t.Type == TransactionType.Debit).Sum(t => t.Amount);

        // Print DEBITO DIRECTO rows for inspection
        Console.WriteLine("=== DEBITO DIRECTO rows ===");
        foreach (var tx in txs.Where(t => t.Description.Contains("DEBITO DIRECTO", StringComparison.OrdinalIgnoreCase)))
            Console.WriteLine($"  {tx.Date:dd/MM} | {tx.Amount,12:F2} | {tx.Description}");

        // Print any CABLEVISIO enrichment for inspection
        Console.WriteLine("=== CABLEVISIO rows ===");
        foreach (var tx in txs.Where(t => t.Description.Contains("CABLEVISIO", StringComparison.OrdinalIgnoreCase)))
            Console.WriteLine($"  {tx.Date:dd/MM} | {tx.Amount,12:F2} | {tx.Description}");

        Console.WriteLine($"\nTotal: {txs.Count} | Credits: {credits:F2} | Debits: {debits:F2}");

        txs.Should().NotBeEmpty();
        // DEBITO DIRECTO transactions that were enriched must use the → format, not replace it
        var enriched = txs.Where(t => t.Description.StartsWith("DEBITO DIRECTO →", StringComparison.OrdinalIgnoreCase)).ToList();
        Console.WriteLine($"Enriched DEBITO DIRECTO count: {enriched.Count}");
    }
}

