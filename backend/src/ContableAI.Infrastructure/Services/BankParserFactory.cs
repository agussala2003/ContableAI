using ContableAI.Domain.Entities;
using ContableAI.Domain.Enums;
using OfficeOpenXml;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ContableAI.Infrastructure.Services;


/// <summary>Parser específico para un banco, soporta CSV y/o XLSX.</summary>
public interface IBankParser
{
    /// <summary>Código único del banco (ej. "BBVA", "GALICIA").</summary>
    string BankCode { get; }

    /// <summary>Nombre para mostrar al usuario.</summary>
    string DisplayName { get; }

    IEnumerable<BankTransaction> Parse(Stream stream, string fileName);
}

// BankParserHelpers — métodos de utilidad compartidos por todos los parsers.

internal static class BankParserHelpers
{
    /// <summary>Parsea dd/MM deduciendo el año (nunca más de 3 meses en el futuro).</summary>
    public static bool TryParseDdMM(string raw, out DateOnly result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var parts = raw.Replace('-', '/').Split('/');
        if (parts.Length < 2) return false;

        if (!int.TryParse(parts[0], out int day)
         || !int.TryParse(parts[1], out int month)
         || day < 1 || day > 31 || month < 1 || month > 12)
            return false;

        var today = DateOnly.FromDateTime(DateTime.Today);
        try
        {
            var candidate = new DateOnly(today.Year, month, day);
            if (candidate > today.AddMonths(3))
                candidate = new DateOnly(today.Year - 1, month, day);
            result = candidate;
            return true;
        }
        catch { return false; }
    }

    /// <summary>Parsea importes argentinos (coma = miles, punto = decimal).</summary>
    public static decimal ParseAmount(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0;
        var clean = raw.Replace(",", "").Replace(" ", "");
        return decimal.TryParse(clean, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)
            ? v : 0;
    }

    /// <summary>Parsea importe de celda Excel (numérica o texto).</summary>
    public static decimal GetXlsxAmount(object? cellValue)
    {
        if (cellValue == null) return 0;
        if (cellValue is double d)   return (decimal)d;
        if (cellValue is decimal dc) return dc;
        return ParseAmount(cellValue.ToString() ?? "");
    }

    /// <summary>Separa una línea CSV respetando campos entre comillas.</summary>
    public static string[] SplitCsvLine(string line, char delimiter = ',')
    {
        var result  = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;

        foreach (char c in line)
        {
            if (c == '"')                { inQuotes = !inQuotes; }
            else if (c == delimiter && !inQuotes)
            { result.Add(current.ToString().Trim('"').Trim()); current.Clear(); }
            else                         { current.Append(c); }
        }
        result.Add(current.ToString().Trim('"').Trim());
        return result.ToArray();
    }

    public static string Col(string[] c, int i) => i < c.Length ? c[i] : string.Empty;

    public static bool ExtIs(string fileName, params string[] exts)
    {
        var ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        return exts.Contains(ext);
    }

    // Detecta si una fecha en col[0] es la cabecera BBVA real
    public static bool IsBbvaRealHeader(string[] cols)
        => cols.Length >= 20
        && cols[0].Equals("FECHA", StringComparison.OrdinalIgnoreCase)
        && cols.Length > 7
        && cols[7].Equals("CONCEPTO", StringComparison.OrdinalIgnoreCase);
}

// BBVA Parser — extracto real multi-columna (CSV y XLSX) y simple 3-col.

public class BbvaParser : IBankParser
{
    public string BankCode    => "BBVA";
    public string DisplayName => "BBVA";

    public IEnumerable<BankTransaction> Parse(Stream stream, string fileName)
        => BankParserHelpers.ExtIs(fileName, ".xlsx", ".xls")
            ? ParseXlsx(stream)
            : ParseCsvAuto(stream);

    private static IEnumerable<BankTransaction> ParseCsvAuto(Stream stream)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        ms.Position = 0;

        if (IsRealFormat(ms))
        { ms.Position = 0; return ParseRealCsv(ms); }

        ms.Position = 0;
        return ParseSimpleCsv(ms);
    }

    private static bool IsRealFormat(Stream stream)
    {
        using var r = new StreamReader(stream, leaveOpen: true);
        for (int i = 0; i < 40; i++)
        {
            var line = r.ReadLine();
            if (line == null) break;
            if (BankParserHelpers.IsBbvaRealHeader(BankParserHelpers.SplitCsvLine(line))) return true;
        }
        return false;
    }

    private static List<BankTransaction> ParseRealCsv(Stream stream)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        var txs = new List<BankTransaction>();
        bool inside = false;
        string? line;

        while ((line = reader.ReadLine()) != null)
        {
            var cols = BankParserHelpers.SplitCsvLine(line);
            if (BankParserHelpers.IsBbvaRealHeader(cols)) { inside = true; continue; }
            if (!inside) continue;
            if (!BankParserHelpers.TryParseDdMM(BankParserHelpers.Col(cols, 0), out var date)) continue;

            var desc = BankParserHelpers.Col(cols, 7).Trim();
            if (string.IsNullOrWhiteSpace(desc) ||
                desc.Equals("SALDO ANTERIOR", StringComparison.OrdinalIgnoreCase)) continue;

            var rawDebit  = BankParserHelpers.Col(cols, 22).Trim();
            var rawCredit = BankParserHelpers.Col(cols, 31).Trim();
            if (string.IsNullOrWhiteSpace(rawDebit) && string.IsNullOrWhiteSpace(rawCredit)) continue;

            decimal amount;
            TransactionType type;

            if (!string.IsNullOrWhiteSpace(rawCredit))
            { amount = Math.Abs(BankParserHelpers.ParseAmount(rawCredit)); type = TransactionType.Credit; }
            else
            { amount = Math.Abs(BankParserHelpers.ParseAmount(rawDebit)); type = TransactionType.Debit; }

            if (amount == 0) continue;
            txs.Add(new BankTransaction { Date = date, Description = desc, Amount = amount, Type = type });
        }
        return txs;
    }

    private static List<BankTransaction> ParseSimpleCsv(Stream stream)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        var txs = new List<BankTransaction>();
        bool header = true;

        while (reader.ReadLine() is { } line)
        {
            if (header) { header = false; continue; } // skip header
            var cols = BankParserHelpers.SplitCsvLine(line);
            if (!DateOnly.TryParseExact(BankParserHelpers.Col(cols, 0)?.Trim(), "dd/MM/yyyy", out var date)) continue;
            var desc = BankParserHelpers.Col(cols, 1).Trim();
            if (string.IsNullOrWhiteSpace(desc)) continue;
            var raw = BankParserHelpers.ParseAmount(BankParserHelpers.Col(cols, 2));
            txs.Add(new BankTransaction
            {
                Date = date, Description = desc,
                Amount = Math.Abs(raw),
                Type   = raw >= 0 ? TransactionType.Credit : TransactionType.Debit,
            });
        }
        return txs;
    }

    private static IEnumerable<BankTransaction> ParseXlsx(Stream stream)
    {
        ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
        using var pkg   = new ExcelPackage(stream);
        var sheet = pkg.Workbook.Worksheets[0];
        if (sheet?.Dimension == null) return [];

        if (sheet.Dimension.Columns >= 20) return ParseXlsxReal(sheet);
        return ParseXlsxSimple(sheet);
    }

    private static List<BankTransaction> ParseXlsxReal(ExcelWorksheet sheet)
    {
        var txs = new List<BankTransaction>();
        bool inside = false;

        for (int r = 1; r <= sheet.Dimension.Rows; r++)
        {
            string Cell(int c) => sheet.Cells[r, c].Value?.ToString()?.Trim() ?? string.Empty;
            var cols = Enumerable.Range(1, Math.Min(sheet.Dimension.Columns, 35))
                                 .Select(Cell).ToArray();

            if (BankParserHelpers.IsBbvaRealHeader(cols)) { inside = true; continue; }
            if (!inside) continue;
            if (!BankParserHelpers.TryParseDdMM(Cell(1), out var date)) continue;

            var desc = Cell(8);
            if (string.IsNullOrWhiteSpace(desc) ||
                desc.Equals("SALDO ANTERIOR", StringComparison.OrdinalIgnoreCase)) continue;

            var credit = BankParserHelpers.GetXlsxAmount(sheet.Cells[r, 32].Value);
            var debit  = BankParserHelpers.GetXlsxAmount(sheet.Cells[r, 23].Value);

            if (credit != 0)
                txs.Add(new BankTransaction { Date = date, Description = desc, Amount = Math.Abs(credit), Type = TransactionType.Credit });
            else if (debit != 0)
                txs.Add(new BankTransaction { Date = date, Description = desc, Amount = Math.Abs(debit), Type = TransactionType.Debit });
        }
        return txs;
    }

    private static List<BankTransaction> ParseXlsxSimple(ExcelWorksheet sheet)
    {
        var txs = new List<BankTransaction>();
        for (int r = 2; r <= sheet.Dimension.Rows; r++)
        {
            string Cell(int c) => sheet.Cells[r, c].Value?.ToString()?.Trim() ?? string.Empty;
            if (!DateOnly.TryParseExact(Cell(1), "dd/MM/yyyy", out var date)) continue;
            var desc = Cell(2);
            if (string.IsNullOrWhiteSpace(desc)) continue;
            var raw = BankParserHelpers.GetXlsxAmount(sheet.Cells[r, 3].Value);
            txs.Add(new BankTransaction
            {
                Date = date, Description = desc,
                Amount = Math.Abs(raw),
                Type   = raw >= 0 ? TransactionType.Credit : TransactionType.Debit,
            });
        }
        return txs;
    }
}

// Galicia Parser — CSV: col[0]=Fecha dd/MM/yyyy, col[2]=Descripcion, col[4]=Importe.

public class GaliciaParser : IBankParser
{
    public string BankCode    => "GALICIA";
    public string DisplayName => "Banco Galicia";

    public IEnumerable<BankTransaction> Parse(Stream stream, string fileName)
        => BankParserHelpers.ExtIs(fileName, ".xlsx", ".xls")
            ? ParseXlsx(stream)
            : ParseCsv(stream);

    // Extracts ExternalId from NAVE/PRISMA description patterns, returns cleaned desc.
    private static (string cleanDesc, string? externalId) ExtractGaliciaExternalId(string raw)
    {
        // NAVE - VENTA CON TARJETA 50194756 [extra]
        // NAVE PAGO CON TRANSFERENCIA 50204403 PCT
        var m = Regex.Match(raw, @"^(NAVE\s*[-–]?\s*[A-Z ]+?)\s+(\d{6,})\s*(.*)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (m.Success)
        {
            var suffix = m.Groups[3].Value.Trim();
            var desc = suffix.Length > 0 ? $"{m.Groups[1].Value.Trim()} {suffix}" : m.Groups[1].Value.Trim();
            return (desc, m.Groups[2].Value);
        }
        // ACREDITAMIENTO PRISMA-COMERCIOS VISA EST.:50194756
        m = Regex.Match(raw, @"^(ACREDITAMIENTO PRISMA[-\w ]*)\s+EST\.:\s*(\d+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (m.Success)
            return (m.Groups[1].Value.Trim(), m.Groups[2].Value);
        return (raw, null);
    }

    private static List<BankTransaction> ParseCsv(Stream stream)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        var txs = new List<BankTransaction>();
        bool header = true;

        while (reader.ReadLine() is { } line)
        {
            if (header) { header = false; continue; }
            var cols = BankParserHelpers.SplitCsvLine(line);
            if (!DateOnly.TryParseExact(BankParserHelpers.Col(cols, 0), "dd/MM/yyyy", out var date)) continue;
            var rawDesc = BankParserHelpers.Col(cols, 2).Trim();
            if (!decimal.TryParse(BankParserHelpers.Col(cols, 4), CultureInfo.InvariantCulture, out var raw)) continue;
            var (desc, extId) = ExtractGaliciaExternalId(rawDesc);
            txs.Add(new BankTransaction
            {
                Date = date, Description = desc,
                Amount = Math.Abs(raw),
                Type   = raw >= 0 ? TransactionType.Credit : TransactionType.Debit,
                ExternalId = extId,
                SourceBank = "GALICIA",
            });
        }
        return txs;
    }

    private static IEnumerable<BankTransaction> ParseXlsx(Stream stream)
    {
        ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
        using var pkg   = new ExcelPackage(stream);
        var sheet = pkg.Workbook.Worksheets[0];
        if (sheet?.Dimension == null) return [];

        var txs = new List<BankTransaction>();
        for (int r = 2; r <= sheet.Dimension.Rows; r++)
        {
            string Cell(int c) => sheet.Cells[r, c].Value?.ToString()?.Trim() ?? string.Empty;
            if (!DateOnly.TryParseExact(Cell(1), "dd/MM/yyyy", out var date)) continue;
            var rawDesc = Cell(3);
            var raw  = BankParserHelpers.GetXlsxAmount(sheet.Cells[r, 5].Value);
            var (desc, extId) = ExtractGaliciaExternalId(rawDesc);
            txs.Add(new BankTransaction
            {
                Date = date, Description = desc,
                Amount = Math.Abs(raw),
                Type   = raw >= 0 ? TransactionType.Credit : TransactionType.Debit,
                ExternalId = extId,
                SourceBank = "GALICIA",
            });
        }
        return txs;
    }
}

// Santander Parser — CSV punto y coma: col[1]=Fecha, col[3]=Descripcion, col[5]=Importe.

public class SantanderParser : IBankParser
{
    public string BankCode    => "SANTANDER";
    public string DisplayName => "Banco Santander";

    public IEnumerable<BankTransaction> Parse(Stream stream, string fileName)
        => BankParserHelpers.ExtIs(fileName, ".xlsx", ".xls")
            ? ParseXlsx(stream)
            : ParseCsv(stream);

    private static List<BankTransaction> ParseCsv(Stream stream)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        var txs = new List<BankTransaction>();
        bool header = true;

        while (reader.ReadLine() is { } line)
        {
            if (header) { header = false; continue; }
            var cols = BankParserHelpers.SplitCsvLine(line, ';');
            if (!DateOnly.TryParseExact(BankParserHelpers.Col(cols, 1), "dd/MM/yyyy", out var date)) continue;
            var desc = BankParserHelpers.Col(cols, 3).Trim();
            if (!decimal.TryParse(BankParserHelpers.Col(cols, 5), CultureInfo.InvariantCulture, out var raw)) continue;
            txs.Add(new BankTransaction
            {
                Date = date, Description = desc,
                Amount = Math.Abs(raw),
                Type   = raw >= 0 ? TransactionType.Credit : TransactionType.Debit,
            });
        }
        return txs;
    }

    private static IEnumerable<BankTransaction> ParseXlsx(Stream stream)
    {
        ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
        using var pkg   = new ExcelPackage(stream);
        var sheet = pkg.Workbook.Worksheets[0];
        if (sheet?.Dimension == null) return [];

        var txs = new List<BankTransaction>();
        for (int r = 2; r <= sheet.Dimension.Rows; r++)
        {
            string Cell(int c) => sheet.Cells[r, c].Value?.ToString()?.Trim() ?? string.Empty;
            if (!DateOnly.TryParseExact(Cell(2), "dd/MM/yyyy", out var date)) continue;
            var desc = Cell(4);
            var raw  = BankParserHelpers.GetXlsxAmount(sheet.Cells[r, 6].Value);
            txs.Add(new BankTransaction
            {
                Date = date, Description = desc,
                Amount = Math.Abs(raw),
                Type   = raw >= 0 ? TransactionType.Credit : TransactionType.Debit,
            });
        }
        return txs;
    }
}

// Macro Parser — col[0]=Fecha, col[3]=Descripcion, col[4]=Debitos, col[5]=Creditos.

public class MacroParser : IBankParser
{
    public string BankCode    => "MACRO";
    public string DisplayName => "Banco Macro";

    public IEnumerable<BankTransaction> Parse(Stream stream, string fileName)
        => BankParserHelpers.ExtIs(fileName, ".xlsx", ".xls")
            ? ParseXlsx(stream)
            : ParseCsv(stream);

    private static List<BankTransaction> ParseCsv(Stream stream)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        var txs = new List<BankTransaction>();
        bool header = true;

        while (reader.ReadLine() is { } line)
        {
            if (header) { header = false; continue; }
            var cols = BankParserHelpers.SplitCsvLine(line);

            if (!DateOnly.TryParseExact(BankParserHelpers.Col(cols, 0), "dd/MM/yyyy", out var date)) continue;
            var desc   = BankParserHelpers.Col(cols, 3).Trim();
            if (string.IsNullOrWhiteSpace(desc)) continue;

            var debito  = BankParserHelpers.ParseAmount(BankParserHelpers.Col(cols, 4));
            var credito = BankParserHelpers.ParseAmount(BankParserHelpers.Col(cols, 5));

            if (credito != 0)
                txs.Add(new BankTransaction { Date = date, Description = desc, Amount = Math.Abs(credito), Type = TransactionType.Credit });
            else if (debito != 0)
                txs.Add(new BankTransaction { Date = date, Description = desc, Amount = Math.Abs(debito), Type = TransactionType.Debit });
        }
        return txs;
    }

    private static IEnumerable<BankTransaction> ParseXlsx(Stream stream)
    {
        ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
        using var pkg   = new ExcelPackage(stream);
        var sheet = pkg.Workbook.Worksheets[0];
        if (sheet?.Dimension == null) return [];

        var txs = new List<BankTransaction>();
        for (int r = 2; r <= sheet.Dimension.Rows; r++)
        {
            string Cell(int c) => sheet.Cells[r, c].Value?.ToString()?.Trim() ?? string.Empty;
            if (!DateOnly.TryParseExact(Cell(1), "dd/MM/yyyy", out var date)) continue;
            var desc    = Cell(4);
            if (string.IsNullOrWhiteSpace(desc)) continue;
            var debito  = BankParserHelpers.GetXlsxAmount(sheet.Cells[r, 5].Value);
            var credito = BankParserHelpers.GetXlsxAmount(sheet.Cells[r, 6].Value);

            if (credito != 0)
                txs.Add(new BankTransaction { Date = date, Description = desc, Amount = Math.Abs(credito), Type = TransactionType.Credit });
            else if (debito != 0)
                txs.Add(new BankTransaction { Date = date, Description = desc, Amount = Math.Abs(debito), Type = TransactionType.Debit });
        }
        return txs;
    }
}

// Banco Nacion Parser — CSV punto y coma: col[0]=Fecha, col[2]=Debito, col[3]=Credito.

public class NacionParser : IBankParser
{
    public string BankCode    => "NACION";
    public string DisplayName => "Banco Nación";

    public IEnumerable<BankTransaction> Parse(Stream stream, string fileName)
        => BankParserHelpers.ExtIs(fileName, ".xlsx", ".xls")
            ? ParseXlsx(stream)
            : ParseCsv(stream);

    private static List<BankTransaction> ParseCsv(Stream stream)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        var txs = new List<BankTransaction>();
        bool header = true;

        while (reader.ReadLine() is { } line)
        {
            if (header) { header = false; continue; }
            var cols = BankParserHelpers.SplitCsvLine(line, ';');
            if (!DateOnly.TryParseExact(BankParserHelpers.Col(cols, 0), "dd/MM/yyyy", out var date)) continue;
            var desc   = BankParserHelpers.Col(cols, 1).Trim();
            if (string.IsNullOrWhiteSpace(desc)) continue;

            var debito  = BankParserHelpers.ParseAmount(BankParserHelpers.Col(cols, 2));
            var credito = BankParserHelpers.ParseAmount(BankParserHelpers.Col(cols, 3));

            if (credito != 0)
                txs.Add(new BankTransaction { Date = date, Description = desc, Amount = Math.Abs(credito), Type = TransactionType.Credit });
            else if (debito != 0)
                txs.Add(new BankTransaction { Date = date, Description = desc, Amount = Math.Abs(debito), Type = TransactionType.Debit });
        }
        return txs;
    }

    private static IEnumerable<BankTransaction> ParseXlsx(Stream stream)
    {
        ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
        using var pkg   = new ExcelPackage(stream);
        var sheet = pkg.Workbook.Worksheets[0];
        if (sheet?.Dimension == null) return [];

        var txs = new List<BankTransaction>();
        for (int r = 2; r <= sheet.Dimension.Rows; r++)
        {
            string Cell(int c) => sheet.Cells[r, c].Value?.ToString()?.Trim() ?? string.Empty;
            if (!DateOnly.TryParseExact(Cell(1), "dd/MM/yyyy", out var date)) continue;
            var desc    = Cell(2);
            if (string.IsNullOrWhiteSpace(desc)) continue;
            var debito  = BankParserHelpers.GetXlsxAmount(sheet.Cells[r, 3].Value);
            var credito = BankParserHelpers.GetXlsxAmount(sheet.Cells[r, 4].Value);

            if (credito != 0)
                txs.Add(new BankTransaction { Date = date, Description = desc, Amount = Math.Abs(credito), Type = TransactionType.Credit });
            else if (debito != 0)
                txs.Add(new BankTransaction { Date = date, Description = desc, Amount = Math.Abs(debito), Type = TransactionType.Debit });
        }
        return txs;
    }
}

// MercadoPago Parser — CSV 'Mis actividades'. Comisiones e impuestos se ignoran (ya neteados en el importe).

public class MercadoPagoParser : IBankParser
{
    public string BankCode    => "MERCADOPAGO";
    public string DisplayName => "MercadoPago";

    // Tipos de operación a ignorar (son cargos internos de MP ya neteados)
    private static readonly string[] SkipTypes = ["comisión", "comision", "impuesto", "cargo", "cuota"];

    public IEnumerable<BankTransaction> Parse(Stream stream, string fileName)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        var txs = new List<BankTransaction>();

        string? headerLine = reader.ReadLine();
        if (headerLine == null) return txs;

        // Detectar delimitador (MP usa punto y coma o coma según la región)
        char delim = headerLine.Contains(';') ? ';' : ',';
        var headers = BankParserHelpers.SplitCsvLine(headerLine, delim);

        // Índices de columnas (búsqueda case-insensitive)
        int iDate   = Array.FindIndex(headers, h => h.Contains("fecha",    StringComparison.OrdinalIgnoreCase));
        int iDesc   = Array.FindIndex(headers, h => h.Contains("descripci", StringComparison.OrdinalIgnoreCase));
        int iType   = Array.FindIndex(headers, h => h.Contains("tipo",     StringComparison.OrdinalIgnoreCase));
        int iAmount = Array.FindIndex(headers, h => h.Contains("importe",  StringComparison.OrdinalIgnoreCase)
                                                 || h.Contains("monto",    StringComparison.OrdinalIgnoreCase));
        // "Detalle" o columnas con "id" / "operaci" pueden contener el ID de la operación
        int iDetail = Array.FindIndex(headers, h => h.Contains("detalle",  StringComparison.OrdinalIgnoreCase)
                                                 || h.Contains("id",       StringComparison.OrdinalIgnoreCase)
                                                 || h.Contains("operaci",  StringComparison.OrdinalIgnoreCase));
        // Evitar que iDetail solape con iType ("tipo de operación" contiene "operaci")
        if (iDetail == iType) iDetail = -1;

        if (iDate < 0 || iAmount < 0) return txs; // no reconocemos el formato

        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var cols = BankParserHelpers.SplitCsvLine(line, delim);

            // Saltar filas de comisiones e impuestos de MP
            if (iType >= 0)
            {
                var tipoOp = BankParserHelpers.Col(cols, iType).ToLowerInvariant();
                if (SkipTypes.Any(s => tipoOp.Contains(s))) continue;
            }

            // Fecha: dd/MM/yyyy o yyyy-MM-dd
            var rawDate = BankParserHelpers.Col(cols, iDate);
            DateOnly date;
            if (!DateOnly.TryParseExact(rawDate, "dd/MM/yyyy", out date) &&
                !DateOnly.TryParseExact(rawDate, "yyyy-MM-dd", out date) &&
                !BankParserHelpers.TryParseDdMM(rawDate, out date))
                continue;

            var desc   = iDesc >= 0 ? BankParserHelpers.Col(cols, iDesc).Trim() : "MercadoPago";
            if (string.IsNullOrWhiteSpace(desc)) desc = "MercadoPago";

            var rawAmount = BankParserHelpers.ParseAmount(BankParserHelpers.Col(cols, iAmount));
            if (rawAmount == 0) continue;

            // Importe negativo = dinero que salió de la cuenta MP (Débito)
            var type   = rawAmount < 0 ? TransactionType.Debit : TransactionType.Credit;

            // Extraer ID de operación desde columna Detalle (si existe y es numérica)
            string? externalId = null;
            if (iDetail >= 0)
            {
                var detailVal = BankParserHelpers.Col(cols, iDetail).Trim();
                if (!string.IsNullOrWhiteSpace(detailVal) &&
                    Regex.IsMatch(detailVal, @"^\d{6,}$"))
                    externalId = detailVal;
            }

            txs.Add(new BankTransaction
            {
                Date       = date,
                Description = desc,
                Amount     = Math.Abs(rawAmount),
                Type       = type,
                SourceBank = "MERCADOPAGO",
                ExternalId = externalId,
            });
        }
        return txs;
    }
}

// Uala Parser — CSV: Fecha de operacion;Descripcion;Importe;Tipo de movimiento.

public class UalaParser : IBankParser
{
    public string BankCode    => "UALA";
    public string DisplayName => "Ualá";

    public IEnumerable<BankTransaction> Parse(Stream stream, string fileName)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        var txs = new List<BankTransaction>();

        string? headerLine = reader.ReadLine();
        if (headerLine == null) return txs;

        char delim = headerLine.Contains(';') ? ';' : ',';
        var headers = BankParserHelpers.SplitCsvLine(headerLine, delim);

        int iDate   = Array.FindIndex(headers, h => h.Contains("fecha",   StringComparison.OrdinalIgnoreCase));
        int iDesc   = Array.FindIndex(headers, h => h.Contains("descripci", StringComparison.OrdinalIgnoreCase)
                                                  || h.Contains("concepto", StringComparison.OrdinalIgnoreCase));
        int iAmount = Array.FindIndex(headers, h => h.Contains("importe",  StringComparison.OrdinalIgnoreCase)
                                                  || h.Contains("monto",   StringComparison.OrdinalIgnoreCase));
        int iType   = Array.FindIndex(headers, h => h.Contains("tipo",     StringComparison.OrdinalIgnoreCase));

        if (iDate < 0 || iAmount < 0) return txs;

        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var cols = BankParserHelpers.SplitCsvLine(line, delim);

            var rawDate = BankParserHelpers.Col(cols, iDate);
            DateOnly date;
            if (!DateOnly.TryParseExact(rawDate, "yyyy-MM-dd", out date) &&
                !DateOnly.TryParseExact(rawDate, "dd/MM/yyyy", out date) &&
                !BankParserHelpers.TryParseDdMM(rawDate, out date))
                continue;

            var desc      = iDesc >= 0 ? BankParserHelpers.Col(cols, iDesc).Trim() : "Ualá";
            if (string.IsNullOrWhiteSpace(desc)) desc = "Ualá";

            var rawAmount = BankParserHelpers.ParseAmount(BankParserHelpers.Col(cols, iAmount));
            if (rawAmount == 0) continue;

            // Columna "Tipo de movimiento": "débito" / "crédito" o inferido por signo
            TransactionType type;
            if (iType >= 0)
            {
                var tipoMov = BankParserHelpers.Col(cols, iType).ToLowerInvariant();
                type = tipoMov.Contains("créd") || tipoMov.Contains("cred") || tipoMov.Contains("ingreso")
                    ? TransactionType.Credit
                    : TransactionType.Debit;
            }
            else
            {
                type = rawAmount >= 0 ? TransactionType.Credit : TransactionType.Debit;
            }

            txs.Add(new BankTransaction { Date = date, Description = desc, Amount = Math.Abs(rawAmount), Type = type });
        }
        return txs;
    }
}


// Credicoop Parser — CSV/XLSX con columnas Fecha, Descripcion, Debito, Credito (separador ;).

public class CredicoopParser : IBankParser
{
    public string BankCode    => "CREDICOOP";
    public string DisplayName => "Banco Credicoop";

    public IEnumerable<BankTransaction> Parse(Stream stream, string fileName)
        => BankParserHelpers.ExtIs(fileName, ".xlsx", ".xls")
            ? ParseXlsx(stream)
            : ParseCsv(stream);

    private static List<BankTransaction> ParseCsv(Stream stream)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        var txs = new List<BankTransaction>();

        string? headerLine = reader.ReadLine();
        if (headerLine == null) return txs;

        char delim = headerLine.Contains(';') ? ';' : ',';
        var headers = BankParserHelpers.SplitCsvLine(headerLine, delim);

        // Detección flexible de columnas por nombre
        int iDate   = Array.FindIndex(headers, h => h.Contains("fecha",       StringComparison.OrdinalIgnoreCase));
        int iDesc   = Array.FindIndex(headers, h => h.Contains("descripci",   StringComparison.OrdinalIgnoreCase)
                                                  || h.Contains("concepto",   StringComparison.OrdinalIgnoreCase)
                                                  || h.Contains("detalle",    StringComparison.OrdinalIgnoreCase));
        int iDebit  = Array.FindIndex(headers, h => h.Contains("debito",      StringComparison.OrdinalIgnoreCase)
                                                  || h.Contains("débito",     StringComparison.OrdinalIgnoreCase)
                                                  || h.Contains("debe",       StringComparison.OrdinalIgnoreCase));
        int iCredit = Array.FindIndex(headers, h => h.Contains("credito",     StringComparison.OrdinalIgnoreCase)
                                                  || h.Contains("crédito",    StringComparison.OrdinalIgnoreCase)
                                                  || h.Contains("haber",      StringComparison.OrdinalIgnoreCase));
        int iAmount = Array.FindIndex(headers, h => h.Contains("importe",     StringComparison.OrdinalIgnoreCase)
                                                  || h.Contains("monto",      StringComparison.OrdinalIgnoreCase));

        // Si no hay encabezado reconocible, usar posiciones fijas Credicoop estándar
        // FECHA;NRO;DESCRIPCION;DEBITOS;CREDITOS;SALDO
        if (iDate < 0)    iDate   = 0;
        if (iDesc < 0)    iDesc   = 2;
        if (iDebit < 0)   iDebit  = 3;
        if (iCredit < 0)  iCredit = 4;

        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var cols = BankParserHelpers.SplitCsvLine(line, delim);

            var rawDate = BankParserHelpers.Col(cols, iDate);
            DateOnly date;
            if (!DateOnly.TryParseExact(rawDate, "dd/MM/yyyy", out date) &&
                !DateOnly.TryParseExact(rawDate, "yyyy-MM-dd", out date) &&
                !BankParserHelpers.TryParseDdMM(rawDate, out date))
                continue;

            var desc = BankParserHelpers.Col(cols, iDesc).Trim();
            if (string.IsNullOrWhiteSpace(desc)) desc = "Credicoop";

            decimal debit  = BankParserHelpers.ParseAmount(BankParserHelpers.Col(cols, iDebit));
            decimal credit = BankParserHelpers.ParseAmount(BankParserHelpers.Col(cols, iCredit));

            // Si sólo hay columna de importe único, usar su signo
            if (debit == 0 && credit == 0 && iAmount >= 0)
            {
                var single = BankParserHelpers.ParseAmount(BankParserHelpers.Col(cols, iAmount));
                if (single == 0) continue;
                txs.Add(new BankTransaction
                {
                    Date        = date,
                    Description = desc,
                    Amount      = Math.Abs(single),
                    Type        = single >= 0 ? TransactionType.Credit : TransactionType.Debit,
                    SourceBank  = "CREDICOOP",
                });
                continue;
            }

            if (credit != 0)
                txs.Add(new BankTransaction { Date = date, Description = desc, Amount = Math.Abs(credit), Type = TransactionType.Credit, SourceBank = "CREDICOOP" });
            else if (debit != 0)
                txs.Add(new BankTransaction { Date = date, Description = desc, Amount = Math.Abs(debit),  Type = TransactionType.Debit,  SourceBank = "CREDICOOP" });
        }
        return txs;
    }

    private static IEnumerable<BankTransaction> ParseXlsx(Stream stream)
    {
        ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
        using var pkg   = new ExcelPackage(stream);
        var sheet = pkg.Workbook.Worksheets[0];
        if (sheet?.Dimension == null) return [];

        var txs = new List<BankTransaction>();
        for (int r = 2; r <= sheet.Dimension.Rows; r++)
        {
            string Cell(int c) => sheet.Cells[r, c].Value?.ToString()?.Trim() ?? string.Empty;
            if (!DateOnly.TryParseExact(Cell(1), "dd/MM/yyyy", out var date)) continue;

            var desc    = Cell(3);
            if (string.IsNullOrWhiteSpace(desc)) desc = "Credicoop";

            var debito  = BankParserHelpers.GetXlsxAmount(sheet.Cells[r, 4].Value);
            var credito = BankParserHelpers.GetXlsxAmount(sheet.Cells[r, 5].Value);

            if (credito != 0)
                txs.Add(new BankTransaction { Date = date, Description = desc, Amount = Math.Abs(credito), Type = TransactionType.Credit, SourceBank = "CREDICOOP" });
            else if (debito != 0)
                txs.Add(new BankTransaction { Date = date, Description = desc, Amount = Math.Abs(debito),  Type = TransactionType.Debit,  SourceBank = "CREDICOOP" });
        }
        return txs;
    }
}


/// <summary>
/// Registra todos los parsers de banco disponibles y selecciona el correcto por bankCode.
/// Para agregar un banco nuevo: crear una clase que implemente <see cref="IBankParser"/>
/// y registrarla en el constructor de esta clase.
/// </summary>
public class BankParserFactory : IBankParserService
{
    private readonly Dictionary<string, IBankParser> _parsers;

    public BankParserFactory()
    {
        var list = new IBankParser[]
        {
            new BbvaParser(),
            new GaliciaParser(),
            new SantanderParser(),
            new MacroParser(),
            new NacionParser(),
            new MercadoPagoParser(),
            new UalaParser(),
            new CredicoopParser(),
            new PdfBankParser(),
        };

        _parsers = list.ToDictionary(
            p => p.BankCode.ToUpperInvariant(),
            p => p);
    }

    /// <summary>Lista de bancos disponibles para exponer al frontend.</summary>
    public IReadOnlyList<(string Code, string DisplayName)> AvailableBanks
        => _parsers.Values
                   .Select(p => (p.BankCode, p.DisplayName))
                   .OrderBy(b => b.DisplayName)
                   .ToList();

    public IEnumerable<BankTransaction> Parse(Stream fileStream, string bankCode, string fileName)
    {
        // PDF files always use the generic PdfBankParser regardless of bankCode
        var ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        if (ext == ".pdf")
            return _parsers["PDF"].Parse(fileStream, fileName!);

        var key = bankCode.Trim().ToUpperInvariant();
        if (_parsers.TryGetValue(key, out var parser))
            return parser.Parse(fileStream, fileName!);

        // Fallback: intentar BBVA simple — bankCode no registrado
        return _parsers["BBVA"].Parse(fileStream, fileName!);
    }

    public IEnumerable<BankTransaction> ParseCsv(Stream fileStream, string bankCode)
        => Parse(fileStream, bankCode, ".csv");
}
