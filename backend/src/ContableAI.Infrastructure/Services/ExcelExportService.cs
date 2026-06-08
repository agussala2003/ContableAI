using ContableAI.Domain.Entities;
using ContableAI.Domain.Enums;
using ClosedXML.Excel;
using System.Globalization;
using System.Text;

namespace ContableAI.Infrastructure.Services;

public interface IExportService
{
    byte[] ExportToExcel(IEnumerable<BankTransaction> transactions, string companyName, int month, int year);
    byte[] ExportJournalEntriesToExcel(IEnumerable<JournalEntry> entries, string companyName, int? month, int? year, IEnumerable<string>? balanceAccounts = null, IReadOnlyDictionary<string, string>? externalCodes = null);

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
    public byte[] ExportJournalEntriesToExcel(IEnumerable<JournalEntry> entries, string companyName, int? month, int? year, IEnumerable<string>? balanceAccounts = null, IReadOnlyDictionary<string, string>? externalCodes = null)
    {
        using var wb = new XLWorkbook();

        var entryList   = entries.OrderBy(e => e.Date).ToList();
        var sheetLabel  = (month.HasValue && year.HasValue) ? $"{month:D2}-{year}" : year.HasValue ? $"{year}" : "todo";
        var periodLabel = (month.HasValue && year.HasValue) ? $"{GetMonthName(month.Value)} {year}"
                        : year.HasValue ? $"{year}" : "Todos los períodos";

        // ── Sheet 1: Formulario de Asiento (consolidated by account) ─────────
        var ws1 = wb.Worksheets.Add("Formulario de Asiento");

        // Title
        ws1.Range("A1:C1").Merge();
        ws1.Cell("A1").Value = "FORMULARIO DE ASIENTO";
        ws1.Cell("A1").Style.Font.Bold = true;
        ws1.Cell("A1").Style.Font.FontSize = 13;
        ws1.Cell("A1").Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        ws1.Cell("A1").Style.Fill.BackgroundColor = XLColor.FromArgb(15, 118, 110);
        ws1.Cell("A1").Style.Font.FontColor = XLColor.White;

        // Company & period meta
        ws1.Cell("A2").Value = "EMPRESA:";  ws1.Cell("A2").Style.Font.Bold = true;
        ws1.Cell("B2").Value = companyName.ToUpper();
        ws1.Cell("A3").Value = "PERÍODO:";  ws1.Cell("A3").Style.Font.Bold = true;
        ws1.Cell("B3").Value = periodLabel.ToUpper();
        ws1.Cell("A4").Value = $"ASIENTOS:"; ws1.Cell("A4").Style.Font.Bold = true;
        ws1.Cell("B4").Value = entryList.Count;

        // Column headers at row 6: A=Descripción, B=Debe, C=Haber, D=Cód.Externo
        var hdr1 = new[] { "Descripción", "Debe", "Haber", "Cód. Externo" };
        for (int i = 0; i < hdr1.Length; i++)
        {
            var c = ws1.Cell(6, i + 1);
            c.Value = hdr1[i];
            c.Style.Font.Bold = true;
            c.Style.Fill.BackgroundColor = XLColor.FromArgb(15, 118, 110);
            c.Style.Font.FontColor = XLColor.White;
            c.Style.Alignment.Horizontal = i == 0 ? XLAlignmentHorizontalValues.Left : XLAlignmentHorizontalValues.Right;
            c.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
        }

        var balanceSet = new HashSet<string>(
            (balanceAccounts ?? []).Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()),
            StringComparer.OrdinalIgnoreCase);

        // Group by account, but split configured balance accounts into exclusive Debe/Haber rows.
        var groups = entryList
            .SelectMany(e => e.Lines.Select(l =>
            {
                var account = l.Account.Trim();
                var isBalance = balanceSet.Contains(account);
                var key = isBalance ? $"{account}__{(l.IsDebit ? "D" : "H")}" : account;
                var label = isBalance ? $"{account} ({(l.IsDebit ? "Debe" : "Haber")})" : account;
                return new { Key = key, Label = label, l.Amount, l.IsDebit };
            }))
            .GroupBy(x => x.Key)
            .Select(g => new
            {
                Key     = g.Key,
                Account = g.First().Label,
                Debe    = g.Where(x => x.IsDebit).Sum(x => x.Amount),
                Haber   = g.Where(x => !x.IsDebit).Sum(x => x.Amount),
            })
            // Mismo orden que la UI: cuentas con Debe primero, luego las de solo Haber
            .OrderBy(g => g.Debe > 0 ? 0 : 1)
            .ThenBy(g => g.Account.Replace(" (Debe)", string.Empty).Replace(" (Haber)", string.Empty), StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Account.EndsWith("(Debe)", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(g => g.Account, StringComparer.OrdinalIgnoreCase)
            .ToList();

        int row1 = 7;
        bool alt1 = false;
        foreach (var g in groups)
        {
            var bg1 = alt1 ? XLColor.FromArgb(240, 253, 251) : XLColor.White;
            // Strip balance suffixes to look up the base account name
            var baseName = g.Account.Replace(" (Debe)", string.Empty).Replace(" (Haber)", string.Empty).Trim();
            var extCode  = externalCodes?.GetValueOrDefault(baseName) ?? string.Empty;
            ws1.Cell(row1, 1).Value = g.Account;
            if (g.Debe  > 0) { ws1.Cell(row1, 2).Value = g.Debe;  ws1.Cell(row1, 2).Style.NumberFormat.Format = "#,##0.00"; ws1.Cell(row1, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right; }
            if (g.Haber > 0) { ws1.Cell(row1, 3).Value = g.Haber; ws1.Cell(row1, 3).Style.NumberFormat.Format = "#,##0.00"; ws1.Cell(row1, 3).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right; }
            if (!string.IsNullOrEmpty(extCode)) ws1.Cell(row1, 4).Value = extCode;
            for (int col = 1; col <= 4; col++)
            {
                ws1.Cell(row1, col).Style.Fill.BackgroundColor = bg1;
                ws1.Cell(row1, col).Style.Border.BottomBorder = XLBorderStyleValues.Hair;
            }
            alt1 = !alt1;
            row1++;
        }

        // Totals
        ws1.Cell(row1, 1).Value = "TOTAL";
        ws1.Cell(row1, 1).Style.Font.Bold = true;
        if (groups.Any())
        {
            ws1.Cell(row1, 2).FormulaA1 = $"SUM(B7:B{row1 - 1})";
            ws1.Cell(row1, 3).FormulaA1 = $"SUM(C7:C{row1 - 1})";
        }
        ws1.Cell(row1, 2).Style.Font.Bold = true;
        ws1.Cell(row1, 3).Style.Font.Bold = true;
        ws1.Cell(row1, 2).Style.NumberFormat.Format = "#,##0.00";
        ws1.Cell(row1, 3).Style.NumberFormat.Format = "#,##0.00";
        ws1.Cell(row1, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        ws1.Cell(row1, 3).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        for (int col = 1; col <= 4; col++)
        {
            ws1.Cell(row1, col).Style.Fill.BackgroundColor = XLColor.FromArgb(226, 232, 240);
            ws1.Cell(row1, col).Style.Border.TopBorder = XLBorderStyleValues.Medium;
        }

        ws1.Column(1).Width = 40;
        ws1.Column(2).Width = 20;
        ws1.Column(3).Width = 20;
        ws1.Column(4).Width = 15;

        // ── Sheet 2: Detalle (individual entries, original format) ───────────
        var ws2 = wb.Worksheets.Add($"Detalle {sheetLabel}");

        ws2.Cell("A1").Value = companyName;
        ws2.Cell("A1").Style.Font.Bold = true;
        ws2.Cell("A1").Style.Font.FontSize = 14;
        ws2.Cell("A2").Value = $"Libro Diario — {periodLabel}";
        ws2.Cell("A2").Style.Font.Italic = true;

        // A=Fecha, B=Descripción, C=Cuenta, D=Cód.Externo, E=Debe, F=Haber
        var hdr2 = new[] { "Fecha", "Descripción", "Cuenta Contable", "Cód. Externo", "Debe", "Haber" };
        for (int i = 0; i < hdr2.Length; i++)
        {
            var cell = ws2.Cell(4, i + 1);
            cell.Value = hdr2[i];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromArgb(13, 110, 101);
            cell.Style.Font.FontColor = XLColor.White;
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        }

        int row2 = 5;
        bool alt2 = false;
        foreach (var entry in entryList)
        {
            var lines = entry.Lines.OrderByDescending(l => l.IsDebit).ToList();
            bool first = true;
            foreach (var line in lines)
            {
                var bg2     = alt2 ? XLColor.FromArgb(240, 253, 251) : XLColor.White;
                var extCode = externalCodes?.GetValueOrDefault(line.Account) ?? string.Empty;
                ws2.Cell(row2, 1).Value = first ? entry.Date.ToString("dd/MM/yyyy") : string.Empty;
                ws2.Cell(row2, 2).Value = first ? entry.Description : string.Empty;
                ws2.Cell(row2, 3).Value = (line.IsDebit ? string.Empty : "        ") + line.Account;
                ws2.Cell(row2, 4).Value = extCode;
                if (line.IsDebit) ws2.Cell(row2, 5).Value = line.Amount;
                else              ws2.Cell(row2, 6).Value = line.Amount;
                ws2.Cell(row2, 5).Style.NumberFormat.Format = "#,##0.00";
                ws2.Cell(row2, 6).Style.NumberFormat.Format = "#,##0.00";
                for (int col = 1; col <= 6; col++)
                    ws2.Cell(row2, col).Style.Fill.BackgroundColor = bg2;
                first = false;
                row2++;
            }
            alt2 = !alt2;
        }

        row2++;
        ws2.Cell(row2, 2).Value = "TOTALES";
        ws2.Cell(row2, 2).Style.Font.Bold = true;
        if (entryList.Any())
        {
            ws2.Cell(row2, 5).FormulaA1 = $"SUM(E5:E{row2 - 2})";
            ws2.Cell(row2, 6).FormulaA1 = $"SUM(F5:F{row2 - 2})";
        }
        ws2.Cell(row2, 5).Style.Font.Bold = true;
        ws2.Cell(row2, 6).Style.Font.Bold = true;
        ws2.Cell(row2, 5).Style.NumberFormat.Format = "#,##0.00";
        ws2.Cell(row2, 6).Style.NumberFormat.Format = "#,##0.00";

        ws2.Columns().AdjustToContents();
        ws2.Column(2).Width = Math.Max(ws2.Column(2).Width, 40);
        ws2.Column(3).Width = Math.Max(ws2.Column(3).Width, 35);
        ws2.Column(4).Width = Math.Max(ws2.Column(4).Width, 15);

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
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
