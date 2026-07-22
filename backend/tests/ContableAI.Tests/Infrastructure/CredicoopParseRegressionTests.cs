using ContableAI.Domain.Enums;
using ContableAI.Infrastructure.Services;
using FluentAssertions;

namespace ContableAI.Tests.Infrastructure;

public class CredicoopParseRegressionTests
{
    private static readonly string CredicoopFolder = TestData.PathTo("extractos", "CREDICOOP");

    [SkippableFact]
    public void Parse_Credicoop_ShouldReturnTransactions_ForAllSamplePdfs()
    {
        var pdfs = TestData.RequirePdfs(CredicoopFolder);
        var parser = new PdfBankParser();

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

    [SkippableFact]
    public void Parse_Credicoop_ShouldNotLeakFooterBoilerplateIntoDescriptions()
    {
        var pdfs = TestData.RequirePdfs(CredicoopFolder);
        var parser = new PdfBankParser();

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

    [SkippableFact]
    public void Parse_Credicoop_ShouldExtractLeadingOperationNumberAsExternalId()
    {
        var pdfs = TestData.RequirePdfs(CredicoopFolder);
        var parser = new PdfBankParser();

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