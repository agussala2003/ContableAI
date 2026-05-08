using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;

namespace ContableAI.Infrastructure.Services;

// Un modelo interno y rápido para representar el VEP o Presentación
public record AfipPresentation(DateOnly Date, string TaxName, decimal Amount);

public interface IAfipParserService
{
    IEnumerable<AfipPresentation> ParsePdf(Stream fileStream);
}

public class PdfAfipParserService : IAfipParserService
{
    // ORDER MATTERS: most specific patterns first. PdfPig can output the full PDF text as a
    // single line (no newlines), so when the description regex over-captures, we must hit the
    // correct mapping before a generic one (e.g. "VCON" before "GANANCIAS").
    private static readonly (string Pattern, string ShortName)[] TaxNameMap =
    [
        ("VCON",             "VEP Consolidado"),   // before CONSOLIDADO and GANANCIAS
        ("CONSOLIDADO",      "VEP Consolidado"),   // Tipo de Pago fallback: "Vep Consolidado ARCA"
        ("HEF",              "Honorarios Fiscales"),
        ("IVA DJ",           "IVA A Pagar"),       // before generic "IVA"
        ("IVA DG",           "IVA A Pagar"),
        ("IMP LEY 25413",    "Imp. Ley 25413"),
        ("IMP.LEY 25413",    "Imp. Ley 25413"),
        ("SIJP",             "Cargas Sociales"),
        ("SICOSS",           "Cargas Sociales"),
        ("CARGAS SOCIALES",  "Cargas Sociales"),
        ("SIRCREB",          "Pago IIBB"),
        ("CM-PY",            "Pago IIBB"),
        ("IIBB",             "Pago IIBB"),
        ("GANANCIAS",        "Impuesto Ganancias"),
    ];

    // Secondary scan: looks for known obligation lines in the full PDF body text
    // Used when Descripción Reducida / Tipo de Pago aren't recognized (e.g. ARCA06/25, MF01/26)
    private static readonly (string Pattern, string ShortName)[] BodyTaxHints =
    [
        ("ASEG.RIESGO",           "Seg. Riesgo Trabajo"),
        ("GANANCIAS SOCIEDADES",  "Impuesto Ganancias"),
        ("IVA (30)",              "IVA A Pagar"),
        ("CONTRIBUCIONES SEG",    "Cargas Sociales"),
        ("EMPLEADOR-APORTES",     "Cargas Sociales"),
        ("FACILIDADES",           "Plan de Facilidades"),
    ];

    private static string NormalizeTaxName(string raw)
    {
        var upper = raw.ToUpperInvariant();
        foreach (var (pattern, shortName) in TaxNameMap)
            if (upper.Contains(pattern, StringComparison.Ordinal))
                return shortName;
        return raw;
    }

    private static string? ScanBodyForTaxName(string fullText)
    {
        var upper = fullText.ToUpperInvariant();
        foreach (var (pattern, name) in BodyTaxHints)
            if (upper.Contains(pattern, StringComparison.Ordinal))
                return name;
        return null;
    }

    /// <summary>
    /// Parsea un comprobante de pago VEP descargado desde AFIP/ARCA en formato PDF.
    /// Soporta:
    ///   - Comprobante de Pago (pagado): extrae Fecha de Pago e IMPORTE PAGADO.
    ///   - Volante Electrónico de Pago pendiente: extrae Fecha Generación e Importe total a pagar.
    ///   - VEPs vencidos sin importe único: se omiten (no se puede determinar el total).
    /// </summary>
    public IEnumerable<AfipPresentation> ParsePdf(Stream fileStream)
    {
        using var ms = new MemoryStream();
        fileStream.CopyTo(ms);
        if (ms.Length == 0) yield break;
        ms.Position = 0;

        PdfDocument pdf;
        try { pdf = PdfDocument.Open(ms); }
        catch { yield break; }

        using (pdf)
        {
            var sb = new StringBuilder();
            foreach (var page in pdf.GetPages())
                sb.AppendLine(page.Text);

            var text = sb.ToString();

            // ── Fecha de pago ──────────────────────────────────────────────
            // Caso principal (Comprobante pagado): "Fecha de Pago: 2025-09-10 Hora: ..."
            var dateMatch = Regex.Match(text, @"Fecha de Pago:\s*(\d{4}-\d{2}-\d{2})", RegexOptions.IgnoreCase);
            DateOnly date;
            if (dateMatch.Success && DateOnly.TryParse(dateMatch.Groups[1].Value.Trim(), out date))
            {
                // date OK
            }
            else
            {
                // Fallback (VEP pendiente): "Fecha Generación: 2026-03-02"
                var dateGen = Regex.Match(text, @"Fecha Generaci[oó]n:\s*(\d{4}-\d{2}-\d{2})", RegexOptions.IgnoreCase);
                if (!dateGen.Success || !DateOnly.TryParse(dateGen.Groups[1].Value.Trim(), out date))
                    yield break;
            }

            // ── Importe ────────────────────────────────────────────────────
            // Caso principal: "IMPORTE PAGADO $11.432.591,13"
            var amountMatch = Regex.Match(text, @"IMPORTE PAGADO\s*\$([0-9.,]+)", RegexOptions.IgnoreCase);
            if (!amountMatch.Success)
                // Fallback (VEP pendiente): "Importe total a pagar $495.750,24"
                amountMatch = Regex.Match(text, @"Importe total a pagar\s*\$([0-9.,]+)", RegexOptions.IgnoreCase);

            if (!amountMatch.Success) yield break;

            // Formato argentino → decimal: "11.432.591,13" → 11432591.13
            var rawAmount = amountMatch.Groups[1].Value.Replace(".", "").Replace(",", ".");
            if (!decimal.TryParse(rawAmount, CultureInfo.InvariantCulture, out var amount))
                yield break;

            // ── Nombre del impuesto ────────────────────────────────────────
            // PdfPig puede concatenar todo el texto de la página en una sola línea larga.
            // Usamos un lookahead para cortar antes del próximo campo conocido (CUIT:, Concepto:, etc.)
            // y evitar que (.+) capture texto de otras secciones (ej: "GANANCIAS SOCIEDADES").
            var nameMatch = Regex.Match(text,
                @"Descripci[oó]n Reducida:\s*(.+?)(?=\s+(?:CUIT[:\s]|Concepto:|Per[ií]odo:|Generado|Datos del))",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!nameMatch.Success)
                // Fallback: captura hasta fin de línea real (PDFs con saltos de línea correctos)
                nameMatch = Regex.Match(text, @"Descripci[oó]n Reducida:\s*([^\r\n]+)", RegexOptions.IgnoreCase);
            if (!nameMatch.Success)
                nameMatch = Regex.Match(text,
                    @"Tipo de Pago:\s*(.+?)(?=\s+(?:Descripci|CUIT[:\s]|Concepto:))",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!nameMatch.Success)
                nameMatch = Regex.Match(text, @"Tipo de Pago:\s*([^\r\n]+)", RegexOptions.IgnoreCase);
            if (!nameMatch.Success) yield break;

            var rawName = nameMatch.Groups[1].Value.Trim();
            var taxName = NormalizeTaxName(rawName);
            // If Descripción Reducida/Tipo de Pago didn't map to a known category (ARCA06/25,
            // AFIP03/24, MF01/26, etc.), scan the PDF body for recognizable obligation lines.
            if (taxName == rawName)
                taxName = ScanBodyForTaxName(text) ?? rawName;
            yield return new AfipPresentation(date, taxName, amount);
        }
    }
}
