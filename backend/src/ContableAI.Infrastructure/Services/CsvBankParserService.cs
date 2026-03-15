using ContableAI.Domain.Entities;
using ContableAI.Domain.Enums;
using CsvHelper;
using CsvHelper.Configuration;
using OfficeOpenXml;
using System.Globalization;

namespace ContableAI.Infrastructure.Services;

public interface IBankParserService
{
    /// <summary>
    /// Parses a bank file (CSV or XLSX). fileName is used to detect format.
    /// </summary>
    IEnumerable<BankTransaction> Parse(Stream fileStream, string bankCode, string fileName);

    // Legacy alias kept for backward compatibility
    IEnumerable<BankTransaction> ParseCsv(Stream fileStream, string bankCode);
}

public class CsvBankParserService : IBankParserService
{
    // ──────────────────────────────────────────────────────────────────────────
    //  Public entry points
    // ──────────────────────────────────────────────────────────────────────────

    public IEnumerable<BankTransaction> Parse(Stream fileStream, string bankCode, string fileName)
    {
        var ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        return (ext == ".xlsx" || ext == ".xls")
            ? ParseXlsx(fileStream, bankCode)
            : ParseCsv(fileStream, bankCode);
    }

    public IEnumerable<BankTransaction> ParseCsv(Stream fileStream, string bankCode)
    {
        // We need to peek at the file to detect BBVA real format vs simple format.
        // Copy stream to memory so we can read twice if needed.
        using var ms = new MemoryStream();
        fileStream.CopyTo(ms);
        ms.Position = 0;

        if (bankCode.ToUpper() == "BBVA" && IsBbvaRealFormat(ms))
        {
            ms.Position = 0;
            return ParseBbvaReal(ms);
        }

        ms.Position = 0;
        return ParseSimpleCsv(ms, bankCode);
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  BBVA real format detection
    // ──────────────────────────────────────────────────────────────────────────

    private static bool IsBbvaRealFormat(Stream stream)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        for (int i = 0; i < 40; i++)
        {
            var line = reader.ReadLine();
            if (line == null) break;

            // A line with many columns (>= 20) that contains FECHA and CONCEPTO
            // is the column-header row of the real BBVA statement
            var cols = SplitCsvLine(line);
            if (cols.Length >= 20
                && cols[0].Equals("FECHA", StringComparison.OrdinalIgnoreCase)
                && cols.Length > 7
                && cols[7].Equals("CONCEPTO", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  BBVA real multi-column CSV (e.g. 0725.csv)
    //  Layout: col[0]=FECHA(dd/MM), col[2]=ORIGEN, col[7]=CONCEPTO,
    //          col[22]=DÉBITO (negative = egreso), col[31]=CRÉDITO
    // ──────────────────────────────────────────────────────────────────────────

    private static IEnumerable<BankTransaction> ParseBbvaReal(Stream stream)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        var transactions = new List<BankTransaction>();
        bool insideData = false;
        string? line;

        while ((line = reader.ReadLine()) != null)
        {
            var cols = SplitCsvLine(line);

            // Mark start of data section when we find the FECHA/CONCEPTO header row
            if (cols.Length >= 8
                && cols[0].Equals("FECHA", StringComparison.OrdinalIgnoreCase)
                && cols[7].Equals("CONCEPTO", StringComparison.OrdinalIgnoreCase))
            {
                insideData = true;
                continue;
            }

            if (!insideData) continue;

            // col[0] must be a dd/MM date to be a transaction row
            var rawDate = Col(cols, 0).Trim();
            if (!TryParseDdMM(rawDate, out var date)) continue;

            var desc = Col(cols, 7).Trim();
            if (string.IsNullOrWhiteSpace(desc)) continue;
            if (desc.Equals("SALDO ANTERIOR", StringComparison.OrdinalIgnoreCase)) continue;

            var rawDebit  = Col(cols, 22).Trim();
            var rawCredit = Col(cols, 31).Trim();

            if (string.IsNullOrWhiteSpace(rawDebit) && string.IsNullOrWhiteSpace(rawCredit))
                continue;

            decimal amount;
            TransactionType type;

            if (!string.IsNullOrWhiteSpace(rawCredit))
            {
                amount = Math.Abs(ParseArgAmount(rawCredit));
                type   = TransactionType.Credit;
            }
            else
            {
                amount = Math.Abs(ParseArgAmount(rawDebit));
                type   = TransactionType.Debit;
            }

            if (amount == 0) continue;

            transactions.Add(new BankTransaction
            {
                Date        = date,
                Description = desc,
                Amount      = amount,
                Type        = type
            });
        }

        return transactions;
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  Simple CSV (banco.csv style – 3 columns + per-bank variants)
    // ──────────────────────────────────────────────────────────────────────────

    private static IEnumerable<BankTransaction> ParseSimpleCsv(Stream stream, string bankCode)
    {
        var delimiter = bankCode.ToUpper() == "SANTANDER" ? ";" : ",";

        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord  = true,
            Delimiter        = delimiter,
            MissingFieldFound = null,
            BadDataFound     = null
        };

        using var reader = new StreamReader(stream, leaveOpen: true);
        using var csv    = new CsvReader(reader, config);

        csv.Read();
        csv.ReadHeader();

        var transactions = new List<BankTransaction>();

        while (csv.Read())
        {
            try
            {
                DateOnly date      = default;
                string   desc      = string.Empty;
                decimal  rawAmount = 0;

                switch (bankCode.ToUpper())
                {
                    case "GALICIA":
                        if (DateOnly.TryParseExact(csv.GetField(0), "dd/MM/yyyy", out date) &&
                            decimal.TryParse(csv.GetField(4), CultureInfo.InvariantCulture, out rawAmount))
                            desc = csv.GetField(2) ?? string.Empty;
                        break;

                    case "SANTANDER":
                        if (DateOnly.TryParseExact(csv.GetField(1), "dd/MM/yyyy", out date) &&
                            decimal.TryParse(csv.GetField(5), CultureInfo.InvariantCulture, out rawAmount))
                            desc = csv.GetField(3) ?? string.Empty;
                        break;

                    case "BBVA":
                    default:
                        // Simple 3-column: Fecha, Descripcion, Importe
                        if (DateOnly.TryParseExact(csv.GetField(0)?.Trim(), "dd/MM/yyyy", out date) &&
                            decimal.TryParse(csv.GetField(2)?.Trim(), NumberStyles.Any,
                                             CultureInfo.InvariantCulture, out rawAmount))
                            desc = csv.GetField(1) ?? string.Empty;
                        break;
                }

                if (date != default && !string.IsNullOrWhiteSpace(desc))
                {
                    transactions.Add(new BankTransaction
                    {
                        Date        = date,
                        Description = desc.Trim(),
                        Amount      = Math.Abs(rawAmount),
                        Type        = rawAmount >= 0 ? TransactionType.Credit : TransactionType.Debit
                    });
                }
            }
            catch { continue; }
        }

        return transactions;
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  XLSX parser using EPPlus
    //  Supports the real BBVA XLSX export (same structure as the CSV) and
    //  a simple 3-column layout for other banks.
    // ──────────────────────────────────────────────────────────────────────────

    private static IEnumerable<BankTransaction> ParseXlsx(Stream stream, string bankCode)
    {
        ExcelPackage.LicenseContext = LicenseContext.NonCommercial;

        using var package = new ExcelPackage(stream);
        var sheet = package.Workbook.Worksheets[0];

        if (sheet == null || sheet.Dimension == null)
            return [];

        int totalCols = sheet.Dimension.Columns;
        int totalRows = sheet.Dimension.Rows;

        // Auto-detect BBVA real layout (>= 20 columns)
        if (bankCode.ToUpper() == "BBVA" && totalCols >= 20)
            return ParseXlsxBbvaReal(sheet, totalRows, totalCols);

        return ParseXlsxSimple(sheet, totalRows, bankCode);
    }

    private static IEnumerable<BankTransaction> ParseXlsxBbvaReal(
        ExcelWorksheet sheet, int totalRows, int totalCols)
    {
        var transactions = new List<BankTransaction>();
        bool insideData = false;

        for (int r = 1; r <= totalRows; r++)
        {
            string CellStr(int col) =>
                sheet.Cells[r, col].Value?.ToString()?.Trim() ?? string.Empty;

            var col1 = CellStr(1);  // FECHA  (1-indexed in EPPlus)
            var col8 = CellStr(8);  // CONCEPTO

            // Detect data section header
            if (col1.Equals("FECHA", StringComparison.OrdinalIgnoreCase)
                && col8.Equals("CONCEPTO", StringComparison.OrdinalIgnoreCase))
            {
                insideData = true;
                continue;
            }

            if (!insideData) continue;

            if (!TryParseDdMM(col1, out var date)) continue;

            var desc = CellStr(8);
            if (string.IsNullOrWhiteSpace(desc)) continue;
            if (desc.Equals("SALDO ANTERIOR", StringComparison.OrdinalIgnoreCase)) continue;

            // EPPlus columns are 1-indexed; CSV col[22]→EPPlus col 23, col[31]→EPPlus col 32
            var rawDebit  = CellStr(23);
            var rawCredit = CellStr(32);

            if (string.IsNullOrEmpty(rawDebit) && string.IsNullOrEmpty(rawCredit)) continue;

            decimal amount;
            TransactionType type;

            if (!string.IsNullOrEmpty(rawCredit))
            {
                // EPPlus may return a numeric value directly
                amount = GetXlsxAmount(sheet.Cells[r, 32].Value);
                type   = TransactionType.Credit;
            }
            else
            {
                amount = Math.Abs(GetXlsxAmount(sheet.Cells[r, 23].Value));
                type   = TransactionType.Debit;
            }

            if (amount == 0) continue;

            transactions.Add(new BankTransaction
            {
                Date        = date,
                Description = desc,
                Amount      = amount,
                Type        = type
            });
        }

        return transactions;
    }

    private static IEnumerable<BankTransaction> ParseXlsxSimple(
        ExcelWorksheet sheet, int totalRows, string bankCode)
    {
        var transactions = new List<BankTransaction>();

        // Skip header row (row 1)
        for (int r = 2; r <= totalRows; r++)
        {
            try
            {
                string Cell(int col) =>
                    sheet.Cells[r, col].Value?.ToString()?.Trim() ?? string.Empty;

                DateOnly date = default;
                string   desc;
                decimal  rawAmount = 0;

                switch (bankCode.ToUpper())
                {
                    case "GALICIA":
                        if (!DateOnly.TryParseExact(Cell(1), "dd/MM/yyyy", out date)) continue;
                        desc = Cell(3);
                        decimal.TryParse(Cell(5), CultureInfo.InvariantCulture, out rawAmount);
                        break;

                    case "SANTANDER":
                        if (!DateOnly.TryParseExact(Cell(2), "dd/MM/yyyy", out date)) continue;
                        desc = Cell(4);
                        decimal.TryParse(Cell(6), CultureInfo.InvariantCulture, out rawAmount);
                        break;

                    default: // BBVA simple
                        if (!DateOnly.TryParseExact(Cell(1), "dd/MM/yyyy", out date)) continue;
                        desc = Cell(2);
                        rawAmount = GetXlsxAmount(sheet.Cells[r, 3].Value);
                        break;
                }

                if (date == default || string.IsNullOrWhiteSpace(desc)) continue;

                transactions.Add(new BankTransaction
                {
                    Date        = date,
                    Description = desc,
                    Amount      = Math.Abs(rawAmount),
                    Type        = rawAmount >= 0 ? TransactionType.Credit : TransactionType.Debit
                });
            }
            catch { continue; }
        }

        return transactions;
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  Helpers
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>Splits a raw CSV line respecting quoted fields.</summary>
    private static string[] SplitCsvLine(string line)
    {
        var result  = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == ',' && !inQuotes)
            {
                result.Add(current.ToString().Trim('"').Trim());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        result.Add(current.ToString().Trim('"').Trim());
        return result.ToArray();
    }

    private static string Col(string[] cols, int index) =>
        index < cols.Length ? cols[index] : string.Empty;

    /// <summary>
    /// Parses dd/MM date and infers the year. If the resulting date would be
    /// more than 3 months in the future, subtracts one year.
    /// </summary>
    private static bool TryParseDdMM(string raw, out DateOnly result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        // Accept dd/MM or dd-MM
        var normalized = raw.Replace('-', '/');
        var parts = normalized.Split('/');
        if (parts.Length < 2) return false;

        if (!int.TryParse(parts[0], out int day)
         || !int.TryParse(parts[1], out int month)
         || day < 1 || day > 31 || month < 1 || month > 12)
            return false;

        var today = DateOnly.FromDateTime(DateTime.Today);

        try
        {
            var candidate = new DateOnly(today.Year, month, day);
            // If the candidate is more than 3 months in the future, use previous year
            if (candidate > today.AddMonths(3))
                candidate = new DateOnly(today.Year - 1, month, day);
            result = candidate;
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Parses Argentine-format numbers (comma = thousands, period = decimal).
    /// Also handles US-format as exported by BBVA CSV: e.g. "-28,114.92".
    /// In practice both formats end up the same after stripping commas.
    /// </summary>
    private static decimal ParseArgAmount(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0;
        // Strip thousands separators (comma), keep decimal point
        var clean = raw.Replace(",", "").Replace(" ", "");
        return decimal.TryParse(clean, NumberStyles.Any, CultureInfo.InvariantCulture, out var val)
            ? val : 0;
    }

    /// <summary>Gets a decimal value from an Excel cell (numeric or string).</summary>
    private static decimal GetXlsxAmount(object? cellValue)
    {
        if (cellValue == null) return 0;
        if (cellValue is double d) return (decimal)d;
        if (cellValue is decimal dc) return dc;
        return ParseArgAmount(cellValue.ToString() ?? "");
    }
}