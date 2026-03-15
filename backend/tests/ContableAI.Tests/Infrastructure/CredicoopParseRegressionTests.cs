using ContableAI.Domain.Enums;
using ContableAI.Infrastructure.Services;
using FluentAssertions;

namespace ContableAI.Tests.Infrastructure;

public class CredicoopParseRegressionTests
{
    private const string CredicoopFolder = @"C:\Users\aguss\Documents\Projects\ContableAI\tests\extractos\CREDICOOP";

    [Fact]
    public void Parse_Credicoop_ShouldReturnTransactions_ForAllSamplePdfs()
    {
        if (!Directory.Exists(CredicoopFolder)) return;

        var parser = new PdfBankParser();
        var pdfs = Directory.EnumerateFiles(CredicoopFolder, "*.pdf", SearchOption.TopDirectoryOnly).OrderBy(p => p).ToList();

        pdfs.Should().NotBeEmpty();

        foreach (var pdfPath in pdfs)
        {
            using var stream = File.OpenRead(pdfPath);
            var txs = parser.Parse(stream, Path.GetFileName(pdfPath)).ToList();
            var ingresos = txs.Where(t => t.Type == TransactionType.Credit).Sum(t => t.Amount);
            var egresos = txs.Where(t => t.Type == TransactionType.Debit).Sum(t => t.Amount);

            Console.WriteLine($"TOTALS | {Path.GetFileName(pdfPath)} | Movimientos={txs.Count} | Ingresos={ingresos:F2} | Egresos={egresos:F2}");

            txs.Should().NotBeEmpty($"{Path.GetFileName(pdfPath)} debería producir movimientos parseados");
            txs.Should().OnlyContain(t => t.SourceBank == "CREDICOOP");
        }
    }

    [Fact]
    public void Parse_Credicoop_ShouldNotLeakFooterBoilerplateIntoDescriptions()
    {
        if (!Directory.Exists(CredicoopFolder)) return;

        var parser = new PdfBankParser();
        var pdfs = Directory.EnumerateFiles(CredicoopFolder, "*.pdf", SearchOption.TopDirectoryOnly).OrderBy(p => p).ToList();

        foreach (var pdfPath in pdfs)
        {
            using var stream = File.OpenRead(pdfPath);
            var txs = parser.Parse(stream, Path.GetFileName(pdfPath)).ToList();

            var leaked = txs
                .Where(t => ContainsFooterBoilerplate(t.Description))
                .Select(t => $"{t.Date:yyyy-MM-dd} | {t.Type} | {t.Amount:F2} | {t.Description}")
                .ToList();

            leaked.Should().BeEmpty($"{Path.GetFileName(pdfPath)} no debe incluir el footer institucional del banco en la descripción");
        }
    }

    [Fact]
    public void Parse_Credicoop_ShouldExtractLeadingOperationNumberAsExternalId()
    {
        if (!Directory.Exists(CredicoopFolder)) return;

        var parser = new PdfBankParser();
        var pdfs = Directory.EnumerateFiles(CredicoopFolder, "*.pdf", SearchOption.TopDirectoryOnly).OrderBy(p => p).ToList();

        var matchedTransactions = new List<string>();

        foreach (var pdfPath in pdfs)
        {
            using var stream = File.OpenRead(pdfPath);
            var txs = parser.Parse(stream, Path.GetFileName(pdfPath)).ToList();

            matchedTransactions.AddRange(txs
                .Where(t => !string.IsNullOrWhiteSpace(t.ExternalId))
                .Select(t => $"{Path.GetFileName(pdfPath)} | {t.ExternalId} | {t.Description}"));

            txs.Where(t => !string.IsNullOrWhiteSpace(t.ExternalId))
                .Should().OnlyContain(t => !t.Description.StartsWith(t.ExternalId!, StringComparison.Ordinal),
                    $"{Path.GetFileName(pdfPath)} debería limpiar el número inicial cuando se usa como id de operación");
        }

        matchedTransactions.Should().NotBeEmpty("al menos algunos movimientos de Credicoop deberían exponer el número inicial como ExternalId");
    }

    private static bool ContainsFooterBoilerplate(string description)
    {
        var upper = description.ToUpperInvariant();
        return upper.Contains("BANCO CREDICOOP COOPERATIVO LIMITADO")
            || upper.Contains("CREDICOOP RESPONDE")
            || upper.Contains("CALIDAD@BANCOCREDICOOP.COOP")
            || upper.Contains("WWW.BANCOCREDICOOP.COOP")
            || upper.Contains("CTRO. DE CONTACTO TELEFONICO");
    }
}