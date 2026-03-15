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
            // Preferimos Descripción Reducida (tiene periodo: "IVA DJ07/25", "HEF-RF", etc.)
            // Fallback a Tipo de Pago si no existe.
            var nameMatch = Regex.Match(text, @"Descripci[oó]n Reducida:\s*(.+)", RegexOptions.IgnoreCase);
            if (!nameMatch.Success)
                nameMatch = Regex.Match(text, @"Tipo de Pago:\s*(.+)", RegexOptions.IgnoreCase);
            if (!nameMatch.Success) yield break;

            var taxName = nameMatch.Groups[1].Value.Trim();
            yield return new AfipPresentation(date, taxName, amount);
        }
    }
}
