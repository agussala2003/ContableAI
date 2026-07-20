using System.Globalization;
using System.Text;
using ContableAI.Infrastructure.Services;
using FluentAssertions;

namespace ContableAI.Tests.Infrastructure;

public class GaliciaParseRegressionTests
{
    private static readonly string GaliciaFolder = TestData.PathTo("extractos", "GALICIA");

    [SkippableFact]
    public void Parse_Galicia_ShouldNotIncludeTotalRetentionSummaryRows()
    {
        var parser = new PdfBankParser();
        var galiciaPdfs = TestData.RequirePdfs(GaliciaFolder);

        foreach (var pdfPath in galiciaPdfs)
        {
            using var stream = File.OpenRead(pdfPath);
            var txs = parser.Parse(stream, Path.GetFileName(pdfPath)).ToList();

            var suspicious = txs
                .Where(t => IsTotalRetentionSummary(t.Description))
                .Select(t => $"{t.Date:yyyy-MM-dd} | {t.Amount:F2} | {t.Description}")
                .ToList();

            suspicious.Should().BeEmpty($"{Path.GetFileName(pdfPath)} no debe importar la fila resumen Total de retencion de impuestos");
        }
    }

    private static bool IsTotalRetentionSummary(string description)
    {
        var normalized = RemoveDiacritics(description).ToUpperInvariant();
        return normalized.Contains("TOTAL") && normalized.Contains("RETENCI") && normalized.Contains("IMPUEST");
    }

    private static string RemoveDiacritics(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var normalized = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}