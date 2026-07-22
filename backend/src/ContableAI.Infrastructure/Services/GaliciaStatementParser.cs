using System.Text.RegularExpressions;
using static ContableAI.Infrastructure.Services.BankParsingHelpers;

namespace ContableAI.Infrastructure.Services;

/// <summary>
/// Parser de extractos Banco Galicia. Sobre el motor tabular agrega: recuperación de la columna
/// crédito cuando el mapeo estándar falla, extracción del id de operación (NAVE / PRISMA) y la
/// exclusión de las filas de totales/retenciones (que en dólares traen tokens de moneda sueltos).
/// </summary>
internal sealed class GaliciaStatementParser : TabularStatementParser
{
    public override string Bank => BankCodes.Galicia;

    protected override double RefineCreditColumn(StatementLine headerRow, string upperLine, double rightCredit)
    {
        if (rightCredit < 0 && upperLine.Contains("CRÉDITO"))
        {
            var cCell = headerRow.Cells.FirstOrDefault(c =>
                c.Text.ToUpperInvariant().Contains("CRÉDITO") || c.Text.ToUpperInvariant().Contains("CREDITO"));
            if (cCell != null) return cCell.Right;
        }
        return rightCredit;
    }

    protected override (string desc, string? extId) ExtractExternalId(string desc) => ExtractGaliciaExternalId(desc);

    protected override bool IsSummaryAmountRow(string rawDesc, int debitCount, int creditCount)
        => IsGaliciaSplitSummaryAmountRow(rawDesc, debitCount, creditCount);

    protected override bool IsSummaryText(string extra)
    {
        if (IsGaliciaRetentionSummaryText(extra))
            return true;

        // Fila con solo la etiqueta "Total" (extractos USD la imprimen en una fila Y aparte de
        // sus importes): no es continuación de la descripción anterior.
        return new string(StripCurrencyTokens(RemoveDiacritics(extra).ToUpperInvariant())
            .Where(char.IsLetter).ToArray()) is "" or "TOTAL" or "TOTALES";
    }

    protected override (string desc, string? extId) MergeContinuationExternalId(string newDesc, string? currentExtId)
        => currentExtId is null ? ExtractGaliciaExternalId(newDesc) : (newDesc, currentExtId);

    // ── Lógica específica Galicia (movida verbatim desde PdfBankParser) ─────────

    private static (string cleanDesc, string? extId) ExtractGaliciaExternalId(string rawDesc)
    {
        var mNave = Regex.Match(rawDesc, @"^(NAVE\s*[-–]?\s*[A-Z ]+?)\s+(\d{6,})\s*(.*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (mNave.Success)
        {
            var suffix = mNave.Groups[3].Value.Trim();
            var desc = suffix.Length > 0 ? $"{mNave.Groups[1].Value.Trim()} {suffix}" : mNave.Groups[1].Value.Trim();
            return (desc, mNave.Groups[2].Value);
        }

        var mPrisma = Regex.Match(rawDesc, @"^(ACREDITAMIENTO PRISMA[-\w ]*)\s+EST\.:\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (mPrisma.Success)
        {
            return (mPrisma.Groups[1].Value.Trim(), mPrisma.Groups[2].Value);
        }

        return (rawDesc, null);
    }

    private static bool IsGaliciaRetentionSummaryText(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;

        var normalized = RemoveDiacritics(line).ToUpperInvariant();
        var compact = new string(normalized.Where(char.IsLetterOrDigit).ToArray());
        return compact.Contains("RETENCI") && compact.Contains("IMPUEST");
    }

    private static bool IsGaliciaSplitSummaryAmountRow(string rawDesc, int debitCount, int creditCount)
    {
        // Todas las filas reales de movimientos de Galicia arrancan con fecha; una fila sin
        // fecha que trae importes solo puede ser la fila de totales (u otro fragmento de resumen).
        if (debitCount == 0 && creditCount == 0) return false;

        var normalized = StripCurrencyTokens(RemoveDiacritics(rawDesc).ToUpperInvariant());
        var letters = new string(normalized.Where(char.IsLetter).ToArray());

        // Filas de solo importes ("$ -$ $" o con solo "TOTAL") son fragmentos de resumen. En
        // dólares los importes vienen con prefijo de moneda en celdas propias, por eso se quitan
        // los tokens USD antes de evaluar las letras restantes.
        return letters.Length == 0 || letters == "TOTAL";
    }
}
