using ContableAI.Domain.Entities;
using ContableAI.Domain.Enums;
using OfficeOpenXml;
using OfficeOpenXml.Style;
using System.Drawing;
using System.Globalization;
using System.Text;

namespace ContableAI.Infrastructure.Services;

public interface IExportService
{
    byte[] ExportToExcel(IEnumerable<BankTransaction> transactions, string companyName, int month, int year);
    byte[] ExportJournalEntriesToExcel(IEnumerable<JournalEntry> entries, string companyName, int? month, int? year, IEnumerable<string>? balanceAccounts = null);

    /// <summary>Genera archivo TXT tab-delimitado compatible con Holistor.</summary>
    byte[] ExportJournalEntriesToHolistor(IEnumerable<JournalEntry> entries);

    /// <summary>Genera archivo CSV separado por punto y coma compatible con Bejerman.</summary>
    byte[] ExportJournalEntriesToBejerman(IEnumerable<JournalEntry> entries);
}

public class ExcelExportService : IExportService
{
    public byte[] ExportToExcel(IEnumerable<BankTransaction> transactions, string companyName, int month, int year)
    {
        ExcelPackage.LicenseContext = LicenseContext.NonCommercial;

        using var package = new ExcelPackage();
        var ws = package.Workbook.Worksheets.Add($"Banco {month:D2}-{year}");

        // =========================================
        // ENCABEZADO DE LA EMPRESA
        // =========================================
        ws.Cells["A1"].Value = companyName;
        ws.Cells["A1"].Style.Font.Bold = true;
        ws.Cells["A1"].Style.Font.Size = 14;
        ws.Cells["A2"].Value = $"Conciliación Bancaria — {GetMonthName(month)} {year}";
        ws.Cells["A2"].Style.Font.Italic = true;

        // =========================================
        // CABECERAS DE COLUMNAS (fila 4)
        // =========================================
        var headers = new[] { "Fecha", "Descripción", "Debe", "Haber", "Cuenta Contable", "Origen" };
        for (int i = 0; i < headers.Length; i++)
        {
            var cell = ws.Cells[4, i + 1];
            cell.Value = headers[i];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.PatternType = ExcelFillStyle.Solid;
            cell.Style.Fill.BackgroundColor.SetColor(Color.FromArgb(63, 63, 153)); // Azul oscuro
            cell.Style.Font.Color.SetColor(Color.White);
            cell.Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
        }

        // =========================================
        // FILAS DE DATOS
        // =========================================
        var txList = transactions.OrderBy(t => t.Date).ToList();
        int row = 5;

        foreach (var tx in txList)
        {
            ws.Cells[row, 1].Value = tx.Date.ToString("dd/MM/yyyy");
            ws.Cells[row, 2].Value = tx.Description;

            if (tx.Type == TransactionType.Debit)
            {
                ws.Cells[row, 3].Value = tx.Amount;  // Debe
                ws.Cells[row, 4].Value = null;        // Haber vacío
            }
            else
            {
                ws.Cells[row, 3].Value = null;        // Debe vacío
                ws.Cells[row, 4].Value = tx.Amount;  // Haber
            }

            ws.Cells[row, 5].Value = tx.AssignedAccount ?? "A Clasificar";
            ws.Cells[row, 6].Value = tx.ClassificationSource;

            // Formato de número para Debe/Haber
            ws.Cells[row, 3].Style.Numberformat.Format = "#,##0.00";
            ws.Cells[row, 4].Style.Numberformat.Format = "#,##0.00";

            // Color por origen
            Color rowColor = tx.ClassificationSource switch
            {
                "AI"        => Color.FromArgb(255, 255, 204), // Amarillo claro — revisión del contador
                "Manual"    => Color.FromArgb(204, 229, 255), // Azul claro — asignación manual
                "AFIP Match"=> Color.FromArgb(229, 204, 255), // Violeta — matched con AFIP
                "Error"     => Color.FromArgb(255, 204, 204), // Rojo claro — sin clasificar
                _           => Color.White                    // Blanco — HardRule (confiable)
            };

            for (int col = 1; col <= 6; col++)
            {
                ws.Cells[row, col].Style.Fill.PatternType = ExcelFillStyle.Solid;
                ws.Cells[row, col].Style.Fill.BackgroundColor.SetColor(rowColor);
            }

            // Bandera violeta: necesita cruce AFIP (todavía pendiente)
            if (tx.NeedsTaxMatching)
            {
                for (int col = 1; col <= 6; col++)
                {
                    ws.Cells[row, col].Style.Fill.BackgroundColor.SetColor(Color.FromArgb(180, 102, 255));
                    ws.Cells[row, col].Style.Font.Color.SetColor(Color.White);
                }
            }

            row++;
        }

        // =========================================
        // FILA DE TOTALES
        // =========================================
        row++;
        ws.Cells[row, 2].Value = "TOTALES";
        ws.Cells[row, 2].Style.Font.Bold = true;

        if (txList.Any())
        {
            ws.Cells[row, 3].Formula = $"SUM(C5:C{row - 2})";
            ws.Cells[row, 4].Formula = $"SUM(D5:D{row - 2})";
        }

        ws.Cells[row, 3].Style.Font.Bold = true;
        ws.Cells[row, 4].Style.Font.Bold = true;
        ws.Cells[row, 3].Style.Numberformat.Format = "#,##0.00";
        ws.Cells[row, 4].Style.Numberformat.Format = "#,##0.00";

        // Saldo resultante (Haber - Debe)
        ws.Cells[row + 1, 2].Value = "SALDO NETO";
        ws.Cells[row + 1, 2].Style.Font.Bold = true;
        ws.Cells[row + 1, 3].Formula = $"D{row}-C{row}";
        ws.Cells[row + 1, 3].Style.Font.Bold = true;
        ws.Cells[row + 1, 3].Style.Numberformat.Format = "#,##0.00";

        // =========================================
        // LEYENDA DE COLORES
        // =========================================
        int legendRow = row + 3;
        ws.Cells[legendRow, 1].Value = "REFERENCIAS";
        ws.Cells[legendRow, 1].Style.Font.Bold = true;

        AddLegendRow(ws, legendRow + 1, Color.White, "Regla automática (HardRule) — 100% confiable");
        AddLegendRow(ws, legendRow + 2, Color.FromArgb(255, 255, 204), "Clasificado por IA — revisar");
        AddLegendRow(ws, legendRow + 3, Color.FromArgb(204, 229, 255), "Asignado manualmente");
        AddLegendRow(ws, legendRow + 4, Color.FromArgb(229, 204, 255), "Cruzado con AFIP");
        AddLegendRow(ws, legendRow + 5, Color.FromArgb(180, 102, 255), "Pendiente de cruce AFIP (bandera violeta)");
        AddLegendRow(ws, legendRow + 6, Color.FromArgb(255, 204, 204), "Sin clasificar — acción requerida");

        // =========================================
        // AUTOFIT COLUMNAS
        // =========================================
        ws.Cells[ws.Dimension.Address].AutoFitColumns();
        ws.Column(2).Width = Math.Max(ws.Column(2).Width, 40); // Descripción más ancha

        return package.GetAsByteArray();
    }

    private static void AddLegendRow(ExcelWorksheet ws, int row, Color color, string label)
    {
        ws.Cells[row, 1].Style.Fill.PatternType = ExcelFillStyle.Solid;
        ws.Cells[row, 1].Style.Fill.BackgroundColor.SetColor(color);
        ws.Cells[row, 1].Value = "     ";
        ws.Cells[row, 2].Value = label;
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
    public byte[] ExportJournalEntriesToExcel(IEnumerable<JournalEntry> entries, string companyName, int? month, int? year, IEnumerable<string>? balanceAccounts = null)
    {
        ExcelPackage.LicenseContext = LicenseContext.NonCommercial;

        using var package = new ExcelPackage();

        var entryList   = entries.OrderBy(e => e.Date).ToList();
        var sheetLabel  = (month.HasValue && year.HasValue) ? $"{month:D2}-{year}" : year.HasValue ? $"{year}" : "todo";
        var periodLabel = (month.HasValue && year.HasValue) ? $"{GetMonthName(month.Value)} {year}"
                        : year.HasValue ? $"{year}" : "Todos los períodos";

        // ── Sheet 1: Formulario de Asiento (consolidated by account) ─────────
        var ws1 = package.Workbook.Worksheets.Add("Formulario de Asiento");

        // Title
        ws1.Cells["A1:C1"].Merge = true;
        ws1.Cells["A1"].Value = "FORMULARIO DE ASIENTO";
        ws1.Cells["A1"].Style.Font.Bold = true;
        ws1.Cells["A1"].Style.Font.Size = 13;
        ws1.Cells["A1"].Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
        ws1.Cells["A1"].Style.Fill.PatternType = ExcelFillStyle.Solid;
        ws1.Cells["A1"].Style.Fill.BackgroundColor.SetColor(Color.FromArgb(15, 118, 110));
        ws1.Cells["A1"].Style.Font.Color.SetColor(Color.White);

        // Company & period meta
        ws1.Cells["A2"].Value = "EMPRESA:";  ws1.Cells["A2"].Style.Font.Bold = true;
        ws1.Cells["B2"].Value = companyName.ToUpper();
        ws1.Cells["A3"].Value = "PERÍODO:";  ws1.Cells["A3"].Style.Font.Bold = true;
        ws1.Cells["B3"].Value = periodLabel.ToUpper();
        ws1.Cells["A4"].Value = $"ASIENTOS:"; ws1.Cells["A4"].Style.Font.Bold = true;
        ws1.Cells["B4"].Value = entryList.Count;

        // Column headers at row 6
        var hdr1 = new[] { "Descripción", "Debe", "Haber" };
        for (int i = 0; i < hdr1.Length; i++)
        {
            var c = ws1.Cells[6, i + 1];
            c.Value = hdr1[i];
            c.Style.Font.Bold = true;
            c.Style.Fill.PatternType = ExcelFillStyle.Solid;
            c.Style.Fill.BackgroundColor.SetColor(Color.FromArgb(15, 118, 110));
            c.Style.Font.Color.SetColor(Color.White);
            c.Style.HorizontalAlignment = i == 0 ? ExcelHorizontalAlignment.Left : ExcelHorizontalAlignment.Right;
            c.Style.Border.Bottom.Style = ExcelBorderStyle.Thin;
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
            .OrderBy(g => g.Account.Replace(" (Debe)", string.Empty).Replace(" (Haber)", string.Empty), StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Account.EndsWith("(Debe)", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(g => g.Account, StringComparer.OrdinalIgnoreCase)
            .ToList();

        int row1 = 7;
        bool alt1 = false;
        foreach (var g in groups)
        {
            var bg1 = alt1 ? Color.FromArgb(240, 253, 251) : Color.White;
            ws1.Cells[row1, 1].Value = g.Account;
            if (g.Debe  > 0) { ws1.Cells[row1, 2].Value = g.Debe;  ws1.Cells[row1, 2].Style.Numberformat.Format = "#,##0.00"; ws1.Cells[row1, 2].Style.HorizontalAlignment = ExcelHorizontalAlignment.Right; }
            if (g.Haber > 0) { ws1.Cells[row1, 3].Value = g.Haber; ws1.Cells[row1, 3].Style.Numberformat.Format = "#,##0.00"; ws1.Cells[row1, 3].Style.HorizontalAlignment = ExcelHorizontalAlignment.Right; }
            for (int col = 1; col <= 3; col++)
            {
                ws1.Cells[row1, col].Style.Fill.PatternType = ExcelFillStyle.Solid;
                ws1.Cells[row1, col].Style.Fill.BackgroundColor.SetColor(bg1);
                ws1.Cells[row1, col].Style.Border.Bottom.Style = ExcelBorderStyle.Hair;
            }
            alt1 = !alt1;
            row1++;
        }

        // Totals
        ws1.Cells[row1, 1].Value = "TOTAL";
        ws1.Cells[row1, 1].Style.Font.Bold = true;
        if (groups.Any())
        {
            ws1.Cells[row1, 2].Formula = $"SUM(B7:B{row1 - 1})";
            ws1.Cells[row1, 3].Formula = $"SUM(C7:C{row1 - 1})";
        }
        ws1.Cells[row1, 2].Style.Font.Bold = true;
        ws1.Cells[row1, 3].Style.Font.Bold = true;
        ws1.Cells[row1, 2].Style.Numberformat.Format = "#,##0.00";
        ws1.Cells[row1, 3].Style.Numberformat.Format = "#,##0.00";
        ws1.Cells[row1, 2].Style.HorizontalAlignment = ExcelHorizontalAlignment.Right;
        ws1.Cells[row1, 3].Style.HorizontalAlignment = ExcelHorizontalAlignment.Right;
        for (int col = 1; col <= 3; col++)
        {
            ws1.Cells[row1, col].Style.Fill.PatternType = ExcelFillStyle.Solid;
            ws1.Cells[row1, col].Style.Fill.BackgroundColor.SetColor(Color.FromArgb(226, 232, 240));
            ws1.Cells[row1, col].Style.Border.Top.Style = ExcelBorderStyle.Medium;
        }

        ws1.Column(1).Width = 40;
        ws1.Column(2).Width = 20;
        ws1.Column(3).Width = 20;

        // ── Sheet 2: Detalle (individual entries, original format) ───────────
        var ws2 = package.Workbook.Worksheets.Add($"Detalle {sheetLabel}");

        ws2.Cells["A1"].Value = companyName;
        ws2.Cells["A1"].Style.Font.Bold = true;
        ws2.Cells["A1"].Style.Font.Size = 14;
        ws2.Cells["A2"].Value = $"Libro Diario — {periodLabel}";
        ws2.Cells["A2"].Style.Font.Italic = true;

        var hdr2 = new[] { "Fecha", "Descripción", "Cuenta Contable", "Debe", "Haber" };
        for (int i = 0; i < hdr2.Length; i++)
        {
            var cell = ws2.Cells[4, i + 1];
            cell.Value = hdr2[i];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.PatternType = ExcelFillStyle.Solid;
            cell.Style.Fill.BackgroundColor.SetColor(Color.FromArgb(13, 110, 101));
            cell.Style.Font.Color.SetColor(Color.White);
            cell.Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
        }

        int row2 = 5;
        bool alt2 = false;
        foreach (var entry in entryList)
        {
            var lines = entry.Lines.OrderByDescending(l => l.IsDebit).ToList();
            bool first = true;
            foreach (var line in lines)
            {
                var bg2 = alt2 ? Color.FromArgb(240, 253, 251) : Color.White;
                ws2.Cells[row2, 1].Value = first ? entry.Date.ToString("dd/MM/yyyy") : string.Empty;
                ws2.Cells[row2, 2].Value = first ? entry.Description : string.Empty;
                ws2.Cells[row2, 3].Value = (line.IsDebit ? string.Empty : "        ") + line.Account;
                ws2.Cells[row2, 4].Value = line.IsDebit  ? line.Amount : (decimal?)null;
                ws2.Cells[row2, 5].Value = !line.IsDebit ? line.Amount : (decimal?)null;
                ws2.Cells[row2, 4].Style.Numberformat.Format = "#,##0.00";
                ws2.Cells[row2, 5].Style.Numberformat.Format = "#,##0.00";
                for (int col = 1; col <= 5; col++)
                {
                    ws2.Cells[row2, col].Style.Fill.PatternType = ExcelFillStyle.Solid;
                    ws2.Cells[row2, col].Style.Fill.BackgroundColor.SetColor(bg2);
                }
                first = false;
                row2++;
            }
            alt2 = !alt2;
        }

        row2++;
        ws2.Cells[row2, 2].Value = "TOTALES";
        ws2.Cells[row2, 2].Style.Font.Bold = true;
        if (entryList.Any())
        {
            ws2.Cells[row2, 4].Formula = $"SUM(D5:D{row2 - 2})";
            ws2.Cells[row2, 5].Formula = $"SUM(E5:E{row2 - 2})";
        }
        ws2.Cells[row2, 4].Style.Font.Bold = true;
        ws2.Cells[row2, 5].Style.Font.Bold = true;
        ws2.Cells[row2, 4].Style.Numberformat.Format = "#,##0.00";
        ws2.Cells[row2, 5].Style.Numberformat.Format = "#,##0.00";

        ws2.Cells[ws2.Dimension.Address].AutoFitColumns();
        ws2.Column(2).Width = Math.Max(ws2.Column(2).Width, 40);
        ws2.Column(3).Width = Math.Max(ws2.Column(3).Width, 35);

        return package.GetAsByteArray();
    }

    // =========================================================================
    // HOLISTOR — TXT tab-delimitado
    // Formato: FECHA \t NRO_ASIENTO \t SUBCUENTA \t DEBE \t HABER \t GLOSA
    // Los importes usan punto como separador decimal (formato invariant).
    // =========================================================================
    public byte[] ExportJournalEntriesToHolistor(IEnumerable<JournalEntry> entries)
    {
        var sb  = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;

        // Encabezado
        sb.AppendLine("FECHA\tNRO_ASIENTO\tSUBCUENTA\tDEBE\tHABER\tGLOSA");

        int nro = 1;
        foreach (var entry in entries.OrderBy(e => e.Date))
        {
            // Dr primero, Cr después
            foreach (var line in entry.Lines.OrderByDescending(l => l.IsDebit))
            {
                var debe  = line.IsDebit  ? line.Amount.ToString("F2", inv) : "0.00";
                var haber = !line.IsDebit ? line.Amount.ToString("F2", inv) : "0.00";
                // Sanear descripción: quitar tabulaciones
                var glosa = entry.Description.Replace("\t", " ");
                sb.AppendLine($"{entry.Date:dd/MM/yyyy}\t{nro}\t{line.Account}\t{debe}\t{haber}\t{glosa}");
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
    public byte[] ExportJournalEntriesToBejerman(IEnumerable<JournalEntry> entries)
    {
        var sb      = new StringBuilder();
        var culture = new CultureInfo("es-AR"); // coma decimal

        // Encabezado
        sb.AppendLine("Fecha;NroAsiento;Cuenta;Debe;Haber;Descripcion");

        int nro = 1;
        foreach (var entry in entries.OrderBy(e => e.Date))
        {
            foreach (var line in entry.Lines.OrderByDescending(l => l.IsDebit))
            {
                var debe  = line.IsDebit  ? line.Amount.ToString("N2", culture) : "0,00";
                var haber = !line.IsDebit ? line.Amount.ToString("N2", culture) : "0,00";
                // Sanear descripción: quitar punto y coma
                var desc  = entry.Description.Replace(";", " ");
                sb.AppendLine($"{entry.Date:dd/MM/yyyy};{nro};{line.Account};{debe};{haber};{desc}");
            }
            nro++;
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }
}
