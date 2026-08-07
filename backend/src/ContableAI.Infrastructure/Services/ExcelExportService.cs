using ContableAI.Domain.Constants;
using ContableAI.Domain.Entities;
using ContableAI.Domain.Enums;
using ClosedXML.Excel;
using System.Globalization;
using System.Text;

namespace ContableAI.Infrastructure.Services;

public interface IExportService
{
    byte[] ExportToExcel(IEnumerable<BankTransaction> transactions, string companyName, int month, int year);
    /// <param name="bankAccountAliases">
    /// Alias por cuenta bancaria. Cuando el conjunto exportado abarca más de una, la hoja
    /// "Formulario de Asiento" desagrega por cuenta y usa estos alias para nombrar cada fila.
    /// </param>
    byte[] ExportJournalEntriesToExcel(IEnumerable<JournalEntry> entries, string companyName, int? month, int? year, IReadOnlyDictionary<string, string>? externalCodes = null, IReadOnlyDictionary<Guid, string>? bankAccountAliases = null);

    /// <summary>Genera archivo TXT tab-delimitado compatible con Holistor.</summary>
    byte[] ExportJournalEntriesToHolistor(IEnumerable<JournalEntry> entries, IReadOnlyDictionary<string, string>? externalCodes = null);

    /// <summary>Genera archivo CSV separado por punto y coma compatible con Bejerman.</summary>
    byte[] ExportJournalEntriesToBejerman(IEnumerable<JournalEntry> entries, IReadOnlyDictionary<string, string>? externalCodes = null);
}

public class ExcelExportService : IExportService
{
    public byte[] ExportToExcel(IEnumerable<BankTransaction> transactions, string companyName, int month, int year)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add($"Banco {month:D2}-{year}");

        // =========================================
        // ENCABEZADO DE LA EMPRESA
        // =========================================
        ws.Cell("A1").Value = companyName;
        ws.Cell("A1").Style.Font.Bold = true;
        ws.Cell("A1").Style.Font.FontSize = 14;
        ws.Cell("A2").Value = $"Conciliación Bancaria — {GetMonthName(month)} {year}";
        ws.Cell("A2").Style.Font.Italic = true;

        // =========================================
        // CABECERAS DE COLUMNAS (fila 4)
        // =========================================
        var headers = new[] { "Fecha", "Descripción", "Debe", "Haber", "Cuenta Contable", "Origen" };
        for (int i = 0; i < headers.Length; i++)
        {
            var cell = ws.Cell(4, i + 1);
            cell.Value = headers[i];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromArgb(63, 63, 153); // Azul oscuro
            cell.Style.Font.FontColor = XLColor.White;
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        }

        // =========================================
        // FILAS DE DATOS
        // =========================================
        var txList = transactions.OrderBy(t => t.Date).ToList();
        int row = 5;

        foreach (var tx in txList)
        {
            ws.Cell(row, 1).Value = tx.Date.ToString("dd/MM/yyyy");
            ws.Cell(row, 2).Value = tx.Description;

            if (tx.Type == TransactionType.Debit)
                ws.Cell(row, 3).Value = tx.Amount;  // Debe (Haber queda vacío)
            else
                ws.Cell(row, 4).Value = tx.Amount;  // Haber (Debe queda vacío)

            ws.Cell(row, 5).Value = tx.AssignedAccount ?? "A Clasificar";
            ws.Cell(row, 6).Value = tx.ClassificationSource;

            // Formato de número para Debe/Haber
            ws.Cell(row, 3).Style.NumberFormat.Format = "#,##0.00";
            ws.Cell(row, 4).Style.NumberFormat.Format = "#,##0.00";

            // Color por origen
            XLColor rowColor = tx.ClassificationSource switch
            {
                "AI"        => XLColor.FromArgb(255, 255, 204), // Amarillo claro — revisión del contador
                "Manual"    => XLColor.FromArgb(204, 229, 255), // Azul claro — asignación manual
                "AFIP Match"=> XLColor.FromArgb(229, 204, 255), // Violeta — matched con AFIP
                "Error"     => XLColor.FromArgb(255, 204, 204), // Rojo claro — sin clasificar
                _           => XLColor.White                    // Blanco — HardRule (confiable)
            };

            for (int col = 1; col <= 6; col++)
                ws.Cell(row, col).Style.Fill.BackgroundColor = rowColor;

            // Bandera violeta: necesita cruce AFIP (todavía pendiente)
            if (tx.NeedsTaxMatching)
            {
                for (int col = 1; col <= 6; col++)
                {
                    ws.Cell(row, col).Style.Fill.BackgroundColor = XLColor.FromArgb(180, 102, 255);
                    ws.Cell(row, col).Style.Font.FontColor = XLColor.White;
                }
            }

            row++;
        }

        // =========================================
        // FILA DE TOTALES
        // =========================================
        row++;
        ws.Cell(row, 2).Value = "TOTALES";
        ws.Cell(row, 2).Style.Font.Bold = true;

        if (txList.Any())
        {
            ws.Cell(row, 3).FormulaA1 = $"SUM(C5:C{row - 2})";
            ws.Cell(row, 4).FormulaA1 = $"SUM(D5:D{row - 2})";
        }

        ws.Cell(row, 3).Style.Font.Bold = true;
        ws.Cell(row, 4).Style.Font.Bold = true;
        ws.Cell(row, 3).Style.NumberFormat.Format = "#,##0.00";
        ws.Cell(row, 4).Style.NumberFormat.Format = "#,##0.00";

        // Saldo resultante (Haber - Debe)
        ws.Cell(row + 1, 2).Value = "SALDO NETO";
        ws.Cell(row + 1, 2).Style.Font.Bold = true;
        ws.Cell(row + 1, 3).FormulaA1 = $"D{row}-C{row}";
        ws.Cell(row + 1, 3).Style.Font.Bold = true;
        ws.Cell(row + 1, 3).Style.NumberFormat.Format = "#,##0.00";

        // =========================================
        // LEYENDA DE COLORES
        // =========================================
        int legendRow = row + 3;
        ws.Cell(legendRow, 1).Value = "REFERENCIAS";
        ws.Cell(legendRow, 1).Style.Font.Bold = true;

        AddLegendRow(ws, legendRow + 1, XLColor.White, "Regla automática (HardRule) — 100% confiable");
        AddLegendRow(ws, legendRow + 2, XLColor.FromArgb(255, 255, 204), "Clasificado por IA — revisar");
        AddLegendRow(ws, legendRow + 3, XLColor.FromArgb(204, 229, 255), "Asignado manualmente");
        AddLegendRow(ws, legendRow + 4, XLColor.FromArgb(229, 204, 255), "Cruzado con AFIP");
        AddLegendRow(ws, legendRow + 5, XLColor.FromArgb(180, 102, 255), "Pendiente de cruce AFIP (bandera violeta)");
        AddLegendRow(ws, legendRow + 6, XLColor.FromArgb(255, 204, 204), "Sin clasificar — acción requerida");

        // =========================================
        // AUTOFIT COLUMNAS
        // =========================================
        ws.Columns().AdjustToContents();
        ws.Column(2).Width = Math.Max(ws.Column(2).Width, 40); // Descripción más ancha

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static void AddLegendRow(IXLWorksheet ws, int row, XLColor color, string label)
    {
        ws.Cell(row, 1).Style.Fill.BackgroundColor = color;
        ws.Cell(row, 1).Value = "     ";
        ws.Cell(row, 2).Value = label;
    }

    private static string GetMonthName(int month) => month switch
    {
        1  => "Enero",   2  => "Febrero",  3  => "Marzo",
        4  => "Abril",   5  => "Mayo",     6  => "Junio",
        7  => "Julio",   8  => "Agosto",   9  => "Septiembre",
        10 => "Octubre", 11 => "Noviembre",12 => "Diciembre",
        _  => month.ToString()
    };

    // =========================================================================
    // LIBRO DIARIO (Journal Entries)
    // =========================================================================
    public byte[] ExportJournalEntriesToExcel(IEnumerable<JournalEntry> entries, string companyName, int? month, int? year, IReadOnlyDictionary<string, string>? externalCodes = null, IReadOnlyDictionary<Guid, string>? bankAccountAliases = null)
    {
        using var wb = new XLWorkbook();

        var entryList   = entries.OrderBy(e => e.Date).ToList();
        var sheetLabel  = (month.HasValue && year.HasValue) ? $"{month:D2}-{year}" : year.HasValue ? $"{year}" : "todo";
        var periodLabel = (month.HasValue && year.HasValue) ? $"{GetMonthName(month.Value)} {year}"
                        : year.HasValue ? $"{year}" : "Todos los períodos";

        // Separar por moneda: los totales de ARS y USD nunca se suman en la misma hoja. Con una
        // sola moneda se conservan los nombres de hoja actuales (sin ruido para las cuentas que
        // operan 100% en pesos); con ambas, se genera un par de hojas por moneda.
        var byCurrency = entryList
            .GroupBy(e => string.IsNullOrWhiteSpace(e.Currency) ? Currencies.Ars : e.Currency)
            .OrderBy(g => g.Key == Currencies.Ars ? 0 : 1)
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

        bool multi = byCurrency.Count > 1;

        if (byCurrency.Count == 0)
        {
            BuildFormularioSheet(wb, "Formulario de Asiento", entryList, companyName, periodLabel, externalCodes, bankAccountAliases);
            BuildDetalleSheet(wb, $"Detalle {sheetLabel}", entryList, companyName, periodLabel, externalCodes);
        }
        else
        {
            foreach (var group in byCurrency)
            {
                var suffix       = multi ? $" {group.Key}" : string.Empty;
                var groupEntries = group.OrderBy(e => e.Date).ToList();
                var groupPeriod  = multi ? $"{periodLabel} — {group.Key}" : periodLabel;
                BuildFormularioSheet(wb, $"Formulario de Asiento{suffix}", groupEntries, companyName, groupPeriod, externalCodes, bankAccountAliases);
                BuildDetalleSheet(wb, $"Detalle {sheetLabel}{suffix}", groupEntries, companyName, groupPeriod, externalCodes);
            }
        }

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    // ── Hoja "Formulario de Asiento" (consolidado por cuenta y lado) para un conjunto de asientos ──
    private static void BuildFormularioSheet(
        XLWorkbook wb, string sheetName, List<JournalEntry> entries, string companyName,
        string periodLabel, IReadOnlyDictionary<string, string>? externalCodes,
        IReadOnlyDictionary<Guid, string>? bankAccountAliases = null)
    {
        var ws = wb.Worksheets.Add(sheetName);

        // Title
        ws.Range("A1:C1").Merge();
        ws.Cell("A1").Value = "FORMULARIO DE ASIENTO";
        ws.Cell("A1").Style.Font.Bold = true;
        ws.Cell("A1").Style.Font.FontSize = 13;
        ws.Cell("A1").Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        ws.Cell("A1").Style.Fill.BackgroundColor = XLColor.FromArgb(15, 118, 110);
        ws.Cell("A1").Style.Font.FontColor = XLColor.White;

        // Company & period meta
        ws.Cell("A2").Value = "EMPRESA:";  ws.Cell("A2").Style.Font.Bold = true;
        ws.Cell("B2").Value = companyName.ToUpper();
        ws.Cell("A3").Value = "PERÍODO:";  ws.Cell("A3").Style.Font.Bold = true;
        ws.Cell("B3").Value = periodLabel.ToUpper();
        ws.Cell("A4").Value = $"ASIENTOS:"; ws.Cell("A4").Style.Font.Bold = true;
        ws.Cell("B4").Value = entries.Count;

        // Column headers at row 6: A=Descripción, B=Debe, C=Haber, D=Cód.Externo
        var hdr1 = new[] { "Descripción", "Debe", "Haber", "Cód. Externo" };
        for (int i = 0; i < hdr1.Length; i++)
        {
            var c = ws.Cell(6, i + 1);
            c.Value = hdr1[i];
            c.Style.Font.Bold = true;
            c.Style.Fill.BackgroundColor = XLColor.FromArgb(15, 118, 110);
            c.Style.Font.FontColor = XLColor.White;
            c.Style.Alignment.Horizontal = i == 0 ? XLAlignmentHorizontalValues.Left : XLAlignmentHorizontalValues.Right;
            c.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
        }

        // Consolidación: SIEMPRE una fila por (cuenta, lado). Una cuenta que recibe importes en el
        // Debe y en el Haber nunca se netea ni se fusiona — se emiten dos filas independientes.
        // Es lo que permite auditar cuentas puente como "VALORES EN TRANSITO": si el Debe no iguala
        // al Haber, falta procesar el otro lado de una transferencia entre cuentas propias.
        //
        // Cuando el conjunto abarca más de una cuenta bancaria, la clave incorpora además la cuenta
        // de origen. Sin eso, una contrapartida como "Gastos Bancarios" sumaría importes de todos
        // los bancos en una sola fila, dando un saldo que no se corresponde con ningún extracto.
        //
        // La condición es la MISMA que decide el desglose en la pantalla (`accountGroups` de
        // journal-page.ts): más de una cuenta bancaria presente en los datos. Por eso no hace falta
        // pasar un flag — con el filtro puesto en una cuenta, el conjunto exportado ya trae una
        // sola y ninguno de los dos desagrega. Si divergieran, el Excel y la pantalla mostrarían
        // distintas filas para el mismo período.
        var splitByBankAccount = entries.Select(e => e.BankAccountId).Distinct().Count() > 1;

        var groups = entries
            .SelectMany(e => e.Lines.Select(l => new { Entry = e, Line = l }))
            .GroupBy(x => new
            {
                Account       = x.Line.Account.Trim(),
                x.Line.IsDebit,
                BankAccountId = splitByBankAccount ? x.Entry.BankAccountId : null,
            })
            .Select(g => new
            {
                g.Key.Account,
                g.Key.IsDebit,
                g.Key.BankAccountId,
                BankAlias = splitByBankAccount
                    ? BankAliasOf(g.Key.BankAccountId, bankAccountAliases)
                    : null,
                Amount = g.Sum(x => x.Line.Amount),
            })
            .ToList();

        // El sufijo "(Debe)"/"(Haber)" solo se agrega cuando la cuenta tiene saldo en ambos lados;
        // en el resto de las cuentas sería ruido en la mayoría de las filas. La comparación se hace
        // dentro del mismo alcance con que se agrupó: desagregado por banco, una cuenta con Debe en
        // uno y Haber en otro no tiene los dos lados en ninguna de las dos filas.
        var twoSided = groups
            .GroupBy(g => $"{g.BankAccountId}|{g.Account.ToLowerInvariant()}", StringComparer.Ordinal)
            .Where(g => g.Any(x => x.IsDebit) && g.Any(x => !x.IsDebit))
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);

        var rows = groups
            .Select(g => new
            {
                g.Account,
                g.IsDebit,
                g.BankAlias,
                Label = BuildLabel(g.Account, g.IsDebit, g.BankAlias,
                    twoSided.Contains($"{g.BankAccountId}|{g.Account.ToLowerInvariant()}")),
                Debe  = g.IsDebit ? g.Amount : 0m,
                Haber = g.IsDebit ? 0m : g.Amount,
            })
            // Mismo orden que la UI: cada cuenta bancaria forma un bloque; dentro de él, primero las
            // filas del Debe y después las del Haber, alfabéticas.
            .OrderBy(g => g.BankAlias ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.IsDebit ? 0 : 1)
            .ThenBy(g => g.Account, StringComparer.OrdinalIgnoreCase)
            .ToList();

        int row1 = 7;
        bool alt1 = false;
        foreach (var g in rows)
        {
            var bg1 = alt1 ? XLColor.FromArgb(240, 253, 251) : XLColor.White;
            var extCode = externalCodes?.GetValueOrDefault(g.Account) ?? string.Empty;
            ws.Cell(row1, 1).Value = g.Label;
            if (g.Debe  > 0) { ws.Cell(row1, 2).Value = g.Debe;  ws.Cell(row1, 2).Style.NumberFormat.Format = "#,##0.00"; ws.Cell(row1, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right; }
            if (g.Haber > 0) { ws.Cell(row1, 3).Value = g.Haber; ws.Cell(row1, 3).Style.NumberFormat.Format = "#,##0.00"; ws.Cell(row1, 3).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right; }
            if (!string.IsNullOrEmpty(extCode)) ws.Cell(row1, 4).Value = extCode;
            for (int col = 1; col <= 4; col++)
            {
                ws.Cell(row1, col).Style.Fill.BackgroundColor = bg1;
                ws.Cell(row1, col).Style.Border.BottomBorder = XLBorderStyleValues.Hair;
            }
            alt1 = !alt1;
            row1++;
        }

        // Totals
        ws.Cell(row1, 1).Value = "TOTAL";
        ws.Cell(row1, 1).Style.Font.Bold = true;
        if (groups.Any())
        {
            ws.Cell(row1, 2).FormulaA1 = $"SUM(B7:B{row1 - 1})";
            ws.Cell(row1, 3).FormulaA1 = $"SUM(C7:C{row1 - 1})";
        }
        ws.Cell(row1, 2).Style.Font.Bold = true;
        ws.Cell(row1, 3).Style.Font.Bold = true;
        ws.Cell(row1, 2).Style.NumberFormat.Format = "#,##0.00";
        ws.Cell(row1, 3).Style.NumberFormat.Format = "#,##0.00";
        ws.Cell(row1, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        ws.Cell(row1, 3).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        for (int col = 1; col <= 4; col++)
        {
            ws.Cell(row1, col).Style.Fill.BackgroundColor = XLColor.FromArgb(226, 232, 240);
            ws.Cell(row1, col).Style.Border.TopBorder = XLBorderStyleValues.Medium;
        }

        ws.Column(1).Width = 40;
        ws.Column(2).Width = 20;
        ws.Column(3).Width = 20;
        ws.Column(4).Width = 15;
    }

    /// <summary>
    /// Alias de la cuenta bancaria de una fila desagregada. Los textos de los dos casos límite
    /// —asiento sin cuenta y cuenta que ya no existe— son los mismos que usa la pantalla: es el
    /// mismo bucket visto desde dos lugares, y nombrarlo distinto rompería la paridad tanto como
    /// agrupar distinto.
    /// </summary>
    private static string BankAliasOf(Guid? bankAccountId, IReadOnlyDictionary<Guid, string>? aliases)
    {
        if (bankAccountId is null) return "Sin cuenta asignada";
        return aliases?.GetValueOrDefault(bankAccountId.Value) ?? "Cuenta desconocida";
    }

    /// <summary>
    /// Etiqueta de la fila. El Formulario tiene una sola columna de texto, así que la cuenta
    /// bancaria va dentro de ella: agregar una columna correría Debe/Haber y rompería la posición
    /// que los contadores ya conocen (y las fórmulas SUM de la fila de totales).
    /// </summary>
    private static string BuildLabel(string account, bool isDebit, string? bankAlias, bool twoSided)
    {
        var label = twoSided ? $"{account} ({(isDebit ? "Debe" : "Haber")})" : account;
        return bankAlias is null ? label : $"{label} [{bankAlias}]";
    }

    // ── Hoja "Detalle" (asientos individuales, formato original) para un conjunto de asientos ──
    private static void BuildDetalleSheet(
        XLWorkbook wb, string sheetName, List<JournalEntry> entries, string companyName,
        string periodLabel, IReadOnlyDictionary<string, string>? externalCodes)
    {
        var ws = wb.Worksheets.Add(sheetName);

        ws.Cell("A1").Value = companyName;
        ws.Cell("A1").Style.Font.Bold = true;
        ws.Cell("A1").Style.Font.FontSize = 14;
        ws.Cell("A2").Value = $"Libro Diario — {periodLabel}";
        ws.Cell("A2").Style.Font.Italic = true;

        // A=Fecha, B=Descripción, C=Cuenta, D=Cód.Externo, E=Debe, F=Haber
        var hdr2 = new[] { "Fecha", "Descripción", "Cuenta Contable", "Cód. Externo", "Debe", "Haber" };
        for (int i = 0; i < hdr2.Length; i++)
        {
            var cell = ws.Cell(4, i + 1);
            cell.Value = hdr2[i];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromArgb(13, 110, 101);
            cell.Style.Font.FontColor = XLColor.White;
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        }

        int row2 = 5;
        bool alt2 = false;
        foreach (var entry in entries)
        {
            var lines = entry.Lines.OrderByDescending(l => l.IsDebit).ToList();
            bool first = true;
            foreach (var line in lines)
            {
                var bg2     = alt2 ? XLColor.FromArgb(240, 253, 251) : XLColor.White;
                var extCode = externalCodes?.GetValueOrDefault(line.Account) ?? string.Empty;
                ws.Cell(row2, 1).Value = first ? entry.Date.ToString("dd/MM/yyyy") : string.Empty;
                ws.Cell(row2, 2).Value = first ? entry.Description : string.Empty;
                ws.Cell(row2, 3).Value = (line.IsDebit ? string.Empty : "        ") + line.Account;
                ws.Cell(row2, 4).Value = extCode;
                if (line.IsDebit) ws.Cell(row2, 5).Value = line.Amount;
                else              ws.Cell(row2, 6).Value = line.Amount;
                ws.Cell(row2, 5).Style.NumberFormat.Format = "#,##0.00";
                ws.Cell(row2, 6).Style.NumberFormat.Format = "#,##0.00";
                for (int col = 1; col <= 6; col++)
                    ws.Cell(row2, col).Style.Fill.BackgroundColor = bg2;
                first = false;
                row2++;
            }
            alt2 = !alt2;
        }

        row2++;
        ws.Cell(row2, 2).Value = "TOTALES";
        ws.Cell(row2, 2).Style.Font.Bold = true;
        if (entries.Any())
        {
            ws.Cell(row2, 5).FormulaA1 = $"SUM(E5:E{row2 - 2})";
            ws.Cell(row2, 6).FormulaA1 = $"SUM(F5:F{row2 - 2})";
        }
        ws.Cell(row2, 5).Style.Font.Bold = true;
        ws.Cell(row2, 6).Style.Font.Bold = true;
        ws.Cell(row2, 5).Style.NumberFormat.Format = "#,##0.00";
        ws.Cell(row2, 6).Style.NumberFormat.Format = "#,##0.00";

        ws.Columns().AdjustToContents();
        ws.Column(2).Width = Math.Max(ws.Column(2).Width, 40);
        ws.Column(3).Width = Math.Max(ws.Column(3).Width, 35);
        ws.Column(4).Width = Math.Max(ws.Column(4).Width, 15);
    }

    // =========================================================================
    // HOLISTOR — TXT tab-delimitado
    // Formato: FECHA \t NRO_ASIENTO \t SUBCUENTA \t DEBE \t HABER \t GLOSA
    // Los importes usan punto como separador decimal (formato invariant).
    // =========================================================================
    public byte[] ExportJournalEntriesToHolistor(IEnumerable<JournalEntry> entries, IReadOnlyDictionary<string, string>? externalCodes = null)
    {
        var sb  = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;

        // Encabezado
        sb.AppendLine("FECHA\tNRO_ASIENTO\tSUBCUENTA\tCOD_EXTERNO\tDEBE\tHABER\tGLOSA");

        int nro = 1;
        foreach (var entry in entries.OrderBy(e => e.Date))
        {
            // Dr primero, Cr después
            foreach (var line in entry.Lines.OrderByDescending(l => l.IsDebit))
            {
                var debe     = line.IsDebit  ? line.Amount.ToString("F2", inv) : "0.00";
                var haber    = !line.IsDebit ? line.Amount.ToString("F2", inv) : "0.00";
                var glosa    = entry.Description.Replace("\t", " ");
                var extCode  = externalCodes?.GetValueOrDefault(line.Account) ?? string.Empty;
                sb.AppendLine($"{entry.Date:dd/MM/yyyy}\t{nro}\t{line.Account}\t{extCode}\t{debe}\t{haber}\t{glosa}");
            }
            nro++;
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    // =========================================================================
    // BEJERMAN — CSV separado por punto y coma
    // Formato: Fecha;NroAsiento;Cuenta;Debe;Haber;Descripcion
    // Los importes usan coma como separador decimal (cultura es-AR).
    // =========================================================================
    public byte[] ExportJournalEntriesToBejerman(IEnumerable<JournalEntry> entries, IReadOnlyDictionary<string, string>? externalCodes = null)
    {
        var sb      = new StringBuilder();
        var culture = new CultureInfo("es-AR"); // coma decimal

        // Encabezado
        sb.AppendLine("Fecha;NroAsiento;Cuenta;CodExterno;Debe;Haber;Descripcion");

        int nro = 1;
        foreach (var entry in entries.OrderBy(e => e.Date))
        {
            foreach (var line in entry.Lines.OrderByDescending(l => l.IsDebit))
            {
                var debe    = line.IsDebit  ? line.Amount.ToString("N2", culture) : "0,00";
                var haber   = !line.IsDebit ? line.Amount.ToString("N2", culture) : "0,00";
                var desc    = entry.Description.Replace(";", " ");
                var extCode = externalCodes?.GetValueOrDefault(line.Account) ?? string.Empty;
                sb.AppendLine($"{entry.Date:dd/MM/yyyy};{nro};{line.Account};{extCode};{debe};{haber};{desc}");
            }
            nro++;
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }
}
