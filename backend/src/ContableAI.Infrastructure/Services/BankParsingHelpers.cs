using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ContableAI.Infrastructure.Services;

/// <summary>
/// Utilidades de parseo compartidas por todos los parsers de extracto (importes argentinos,
/// fechas, columnas, detección de período y filas de fin de tabla). Son bank-agnósticas: no
/// contienen ninguna regla específica de un banco. Se consumen con <c>using static</c> para que
/// cada parser las invoque sin calificador, igual que cuando vivían dentro de PdfBankParser.
/// </summary>
internal static class BankParsingHelpers
{
    // ── Expresiones regulares ──────────────────────────────────────────────────

    /// <summary>Detecta fechas en formato dd/mm, dd-mm, dd/mm/yy o dd/mm/yyyy.</summary>
    public static readonly Regex RxDate = new(
        @"^(\d{1,2})[/\-](\d{1,2})(?:[/\-](\d{2,4}))?$", RegexOptions.Compiled);

    /// <summary>Importes estándar argentinos (ej. 1.234,56 o -1.234,56).</summary>
    public static readonly Regex RxAmount = new(
        @"^-?[\d]{1,3}(?:\.[\d]{3})*,\d{2}$", RegexOptions.Compiled);

    /// <summary>Importes cortos sin separador de miles (ej. 1234,56).</summary>
    public static readonly Regex RxAmountShort = new(
        @"^-?\d+,\d{2}$", RegexOptions.Compiled);

    /// <summary>Importes en formato US (coma=miles, punto=decimal) — usado por Banco Ciudad.</summary>
    public static readonly Regex RxCiudadAmount = new(
        @"^-?\d+(?:,\d{3})*\.\d{2}$", RegexOptions.Compiled);

    // Tokens de moneda que Galicia imprime como celdas sueltas junto a los importes en
    // extractos en dólares ("USD", "-USD", "US$", "U$S"). El "$" de pesos no lleva letras.
    private static readonly Regex RxCurrencyToken = new(
        @"(?<![A-Z])(?:USD|US\$|U\$S)(?![A-Z])", RegexOptions.Compiled);

    // ── Importes ───────────────────────────────────────────────────────────────

    public static bool IsArgentineAmount(string text) =>
        !string.IsNullOrWhiteSpace(text) && (RxAmount.IsMatch(text) || RxAmountShort.IsMatch(text));

    public static decimal ParseArgentineAmount(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0;

        var clean = raw.Trim().Replace(".", "").Replace(",", ".");
        return decimal.TryParse(clean, NumberStyles.Any, CultureInfo.InvariantCulture, out var result)
            ? result
            : 0;
    }

    public static List<decimal> ExtractAmountsFromText(string text)
    {
        var rx = new Regex(@"-?[\d]{1,3}(?:\.[\d]{3})*,\d{2}|-?\d+,\d{2}");
        return rx.Matches(text)
            .Select(m => ParseArgentineAmount(m.Value))
            .Where(v => v != 0)
            .ToList();
    }

    // ── Texto y celdas ─────────────────────────────────────────────────────────

    public static string JoinCells(StatementLine row) => string.Join(" ", row.Cells.Select(c => c.Text)).Trim();

    public static string StripCurrencyTokens(string text) => RxCurrencyToken.Replace(text, " ");

    public static string RemoveDiacritics(string text)
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

    public static double FindColumnX(StatementLine row, string[] names)
    {
        foreach (var cell in row.Cells)
        {
            if (names.Any(n => cell.Text.Contains(n, StringComparison.OrdinalIgnoreCase)))
                return cell.X;
        }
        return -1;
    }

    // ── Fechas y período del extracto ──────────────────────────────────────────

    public static bool TryParseDate(string text, out DateOnly date, int? referenceYear = null, int? primaryMonth = null)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var token = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? text;
        var m = RxDate.Match(token);
        if (!m.Success) return false;

        if (!int.TryParse(m.Groups[1].Value, out var day)   || day   < 1 || day   > 31) return false;
        if (!int.TryParse(m.Groups[2].Value, out var month) || month < 1 || month > 12) return false;

        int year;
        if (m.Groups[3].Success && int.TryParse(m.Groups[3].Value, out var y))
        {
            year = y < 100 ? 2000 + y : y;
        }
        else
        {
            year = referenceYear ?? DateTime.Today.Year;
        }

        try
        {
            date = new DateOnly(year, month, day);

            // Corrección cross-year forward: diciembre en extractos cuyo mes principal es enero o febrero.
            if (referenceYear.HasValue && primaryMonth.HasValue
                && month == 12 && primaryMonth.Value <= 2 && year == referenceYear.Value)
            {
                date = new DateOnly(year - 1, month, day);
            }

            // Corrección cross-year inversa: enero/febrero en extractos cuyo mes principal es noviembre/diciembre.
            if (referenceYear.HasValue && primaryMonth.HasValue
                && month <= 2 && primaryMonth.Value >= 11 && year == referenceYear.Value)
            {
                date = new DateOnly(year + 1, month, day);
            }

            // Heurística: sin año de referencia y más de 3 meses en el futuro → fin del año pasado.
            if (referenceYear == null && date > DateOnly.FromDateTime(DateTime.Today.AddMonths(3)))
            {
                date = new DateOnly(year - 1, month, day);
            }

            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Devuelve el año y mes del período principal del extracto. El nombre del archivo es la
    /// fuente autoritativa; si no contiene un período reconocible, se cae al mes/año MÁS FRECUENTE
    /// entre las fechas explícitas del documento (nunca la fecha máxima, para no dejarse desviar
    /// por avisos con fechas futuras).
    /// </summary>
    public static (int? stmtYear, int? primaryMonth) DetectStatementInfo(List<StatementLine> rows, string? fileName = null)
    {
        if (TryParsePeriodFromFileName(fileName, out var fromName))
            return fromName;

        var rx = new Regex(@"(\d{1,2})[/\-](\d{1,2})[/\-](\d{2,4})");
        var counts = new Dictionary<(int Year, int Month), int>();

        foreach (var row in rows)
        {
            foreach (var cell in row.Cells)
            {
                foreach (Match m in rx.Matches(cell.Text.Trim()))
                {
                    if (!int.TryParse(m.Groups[3].Value, out var y)) continue;
                    if (y < 100) y += 2000;
                    if (y < 2020 || y > DateTime.Today.Year + 1) continue;

                    if (!int.TryParse(m.Groups[2].Value, out var mo) || mo < 1 || mo > 12) continue;

                    counts[(y, mo)] = counts.GetValueOrDefault((y, mo)) + 1;
                }
            }
        }

        if (counts.Count == 0) return (null, null);

        var best = counts
            .OrderByDescending(kvp => kvp.Value)
            .ThenByDescending(kvp => kvp.Key.Year * 12 + kvp.Key.Month)
            .First().Key;

        return (best.Year, best.Month);
    }

    /// <summary>
    /// Extrae el período (mes, año) del nombre del archivo. Acepta "MMYYYY"/"MMYY" exactos,
    /// "MM.YYYY"/"MM YYYY"/"MM-YYYY", "YYYY.MM"/"YYYY_MM", y tokens MMYYYY/MMYY sueltos. Todo
    /// match se valida: mes 1..12, año 2020..hoy+1.
    /// </summary>
    public static bool TryParsePeriodFromFileName(string? fileName, out (int? stmtYear, int? primaryMonth) period)
    {
        period = (null, null);
        if (string.IsNullOrEmpty(fileName)) return false;

        var rawName = Path.GetFileNameWithoutExtension(fileName);

        // "_20"/"%20" son espacios URL-encodeados (ej. "BBVA_20TB_2012.2024.pdf" → "BBVA TB 12.2024.pdf").
        var sanitizedName = rawName.Replace("_20", " ").Replace("%20", " ");

        (string Pattern, bool YearFirst)[] patterns =
        [
            (@"^(\d{2})(\d{4})$",                    false), // "012025"
            (@"^(\d{2})(\d{2})$",                    false), // "0125"
            (@"(?<!\d)(\d{1,2})[._ -](\d{4})(?!\d)", false), // "11.2024", "05 2025"
            (@"(?<!\d)(20\d{2})[._ -](\d{1,2})(?!\d)", true), // "2024_11"
            (@"(?<!\d)(\d{2})(\d{4})(?!\d)",         false), // "...012025..."
            (@"(?<!\d)(\d{2})(\d{2})(?!\d)",         false), // "1225 (1)"
        ];

        string[] candidates = rawName == sanitizedName ? [rawName] : [rawName, sanitizedName];

        foreach (var baseName in candidates)
        {
            foreach (var (pattern, yearFirst) in patterns)
            {
                foreach (Match m in Regex.Matches(baseName, pattern))
                {
                    var moText = yearFirst ? m.Groups[2].Value : m.Groups[1].Value;
                    var yText  = yearFirst ? m.Groups[1].Value : m.Groups[2].Value;

                    if (!int.TryParse(moText, out var mo) || mo < 1 || mo > 12) continue;
                    if (!int.TryParse(yText, out var y)) continue;
                    if (y < 100) y += 2000;
                    if (y < 2020 || y > DateTime.Today.Year + 1) continue;

                    period = (y, mo);
                    return true;
                }
            }
        }

        return false;
    }

    // ── Fin de tabla / filas irrelevantes ──────────────────────────────────────

    public static bool IsIrrelevantLine(string text)
    {
        var upper = text.ToUpperInvariant();
        if (upper.Contains("SALDO ANTERIOR") || upper.Contains("SALDO INICIAL") || upper.Contains("SALDO FINAL")) return true;
        if (upper.Contains("TOTAL MENSUAL") || upper.Contains("TOTALES")) return true;
        if (upper.Contains("PERIODO COMPRENDIDO")) return true;
        if (upper.StartsWith("PAGINA") || upper.StartsWith("PÁGINA") || upper.Contains("PAGINA 0") || upper.Contains("PAGE ")) return true;
        if ((upper.Contains("TRANSPORTE") || upper.Contains("TRANSPORTA")) && !upper.Contains("SUBE") && !upper.Contains("CARGA")) return true;
        if (upper.Contains("VIENE DE PAGINA") || upper.Contains("CONTINUA EN PAGINA")) return true;
        if (upper.Contains("RESUMEN DE CUENTA") || upper.Contains("CUENTA CORRIENTE") || upper.Contains("CBU DE SU CUENTA")) return true;
        if (upper.Contains("BANCO CREDICOOP COOPERATIVO LIMITADO")) return true;
        if (upper.Contains("CREDICOOP RESPONDE") || upper.Contains("CALIDAD@BANCOCREDICOOP.COOP")) return true;
        if (upper.Contains("CTRO. DE CONTACTO TELEFONICO") || upper.Contains("WWW.BANCOCREDICOOP.COOP")) return true;

        return false;
    }

    public static bool IsEndOfTableMarker(string upperLine)
    {
        return upperLine.StartsWith("SALDO AL") ||
               upperLine.Contains("DEBITOS AUTOMATICOS DEL") ||
               upperLine.Contains("CABAL DEBITO DEL") ||
               upperLine.Contains("TRANSFERENCIAS PESOS DEL") ||
               upperLine.Contains("NRO DE CHEQUE") ||
               (upperLine.Contains("IMPUESTO LEY") && upperLine.Contains("TOTAL")) ||
               upperLine.Contains("PERIODO COMPRENDIDO ENTRE") ||
               upperLine.Contains("LOS DEPÓSITOS EN PESOS") ||
               upperLine.Contains("LOS DEPOSITOS EN PESOS") ||
               upperLine.Contains("TOTAL MENSUAL RETENCION") ||
               upperLine.Contains("CANALES DE ATENCIÓN") ||
               upperLine.Contains("CANALES DE ATENCION") ||
               upperLine.Contains("LIMITAN SU RESPONSABILIDAD") ||
               upperLine.Contains("LAS INVERSIONES EN CUOTAPARTES") ||
               upperLine.Contains("LA SIGUIENTE INFORMACION APLICA") ||
               upperLine.Contains("LA SIGUIENTE INFORMACIÓN APLICA") ||
               upperLine.Contains("TODOS LOS PRECIOS INFORMADOS") ||
               upperLine.Contains("TARJETAS DE DÉBITO DE EMPRESAS:") ||
               upperLine.Contains("TARJETAS DE DEBITO DE EMPRESAS:") ||
               upperLine.Contains("ESTIMADO CLIENTE") ||
               upperLine.Contains("NOS COMUNICAMOS PARA INFORMARTE") ||
               upperLine.Contains("SALDO CONSOLIDADO") ||
               upperLine.Contains("TOTAL MOVIMIENTOS") ||
               IsGaliciaRetentionSummaryRow(upperLine) ||
               upperLine.Contains("EL CREDITO DE IMPUESTO") ||
               upperLine.Contains("IMPUESTO A LOS DEBITOS") ||
               upperLine.Contains("IMPUESTO A LOS DÉBITOS") ||
               upperLine.Contains("REGIMEN SISTEMA SIRCREB") ||
               upperLine.Contains("RÉGIMEN SISTEMA SIRCREB");
    }

    // Galicia often renders this row with broken spacing/accent glyphs; normalize aggressively.
    public static bool IsGaliciaRetentionSummaryRow(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;

        var normalized = RemoveDiacritics(line).ToUpperInvariant();
        var compact = new string(normalized.Where(char.IsLetterOrDigit).ToArray());
        return compact.Contains("TOTAL") && compact.Contains("RETENCI") && compact.Contains("IMPUEST");
    }
}
