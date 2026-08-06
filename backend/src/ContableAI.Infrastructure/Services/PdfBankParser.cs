using ContableAI.Domain.Constants;
using ContableAI.Domain.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.RegularExpressions;
using static ContableAI.Infrastructure.Services.BankParsingHelpers;

namespace ContableAI.Infrastructure.Services;

/// <summary>
/// Orquestador de la lectura de extractos PDF. Coordina, sin conocer los detalles de ningún banco:
///   1. la lectura física (<see cref="IStatementTextExtractor"/>) → líneas posicionadas + banco,
///   2. la normalización de filas partidas (solo ruta digital),
///   3. la detección de moneda a nivel documento,
///   4. el despacho a la estrategia del banco (<see cref="IBankStatementParser"/>).
///
/// Agregar un banco nuevo es crear una implementación de <see cref="IBankStatementParser"/> y
/// registrarla en el diccionario de estrategias — sin tocar este orquestador (Open/Closed).
/// </summary>
public class PdfBankParser : IBankParser
{
    private readonly IStatementTextExtractor _extractor;
    private readonly ILogger<PdfBankParser> _logger;
    private readonly IReadOnlyDictionary<string, IBankStatementParser> _bankParsers;
    private readonly IBankStatementParser _genericParser;

    // internal: recibe la abstracción (interna a Infrastructure). La componen el factory y los
    // tests (InternalsVisibleTo). Producción nunca instancia el parser por fuera del factory.
    internal PdfBankParser(IStatementTextExtractor extractor, ILogger<PdfBankParser> logger)
    {
        _extractor = extractor;
        _logger    = logger;

        _genericParser = new GenericStatementParser();
        IBankStatementParser[] parsers =
        [
            new BbvaStatementParser(),
            new GaliciaStatementParser(),
            new CredicoopStatementParser(),
            new MercadoPagoStatementParser(),
            new CiudadStatementParser(logger),
            _genericParser,
        ];
        _bankParsers = parsers.ToDictionary(p => p.Bank, p => p);
    }

    // Sobrecargas de conveniencia: usan el extractor real (OCR/PdfPig). Se mantienen para el
    // factory y para los tests de regresión con PDFs reales, que instancian el parser sin DI.
    public PdfBankParser(ILogger<PdfBankParser> logger) : this(new OcrStatementExtractor(), logger) { }
    public PdfBankParser() : this(new OcrStatementExtractor(), NullLogger<PdfBankParser>.Instance) { }

    public string BankCode    => "PDF";
    public string DisplayName => "PDF (extracto bancario)";

    public IEnumerable<BankTransaction> Parse(Stream stream, string fileName)
        => ParseStatement(stream, fileName).Transactions;

    public ParsedStatement ParseStatement(Stream stream, string fileName)
    {
        try
        {
            // 1. Lectura física delegada al extractor: líneas posicionadas + banco + cómo se leyó.
            var doc = _extractor.Extract(stream, fileName);

            // 2. El merge de filas partidas solo aplica a la ruta digital (donde el bucketeo por Y
            //    puede partir una fila en dos). En OCR las filas ya vienen espaciadas.
            var rows = doc.Source == StatementSource.Digital
                ? MergeSplitAmountRows([.. doc.Lines])
                : [.. doc.Lines];

            _logger.LogDebug("[PDF] Banco final: {Bank} — total filas: {Rows}", doc.Bank, rows.Count);

            // 3. Detección de moneda a nivel documento (aborta si el extracto mezcla monedas).
            var currency = DetectCurrency(rows);
            _logger.LogDebug("[PDF] Moneda detectada: {Currency}", currency);

            // 4. Identificación de la cuenta a nivel documento, para el enrutamiento automático.
            var (accountNumber, cbu) = DetectAccountIdentifiers(rows);
            _logger.LogDebug("[PDF] Cuenta detectada: {Account} — CBU: {Cbu}", accountNumber, cbu);

            // 5. Despacho a la estrategia del banco (o la genérica si no hay una específica).
            var parser = _bankParsers.GetValueOrDefault(doc.Bank) ?? _genericParser;
            var txs = parser.Parse(rows, fileName).ToList();

            // Post-pass: estampar la moneda detectada en todas las transacciones del extracto.
            foreach (var tx in txs)
                tx.Currency = currency;

            return new ParsedStatement(txs, currency, doc.Bank, accountNumber, cbu);
        }
        catch (InvalidOperationException)
        {
            // Mensajes aptos para el usuario (tamaño, OCR no disponible, extracto mixto): se propagan.
            throw;
        }
        catch
        {
            // PDF corrupto/ilegible u otro fallo inesperado: sin transacciones (igual que antes).
            return new ParsedStatement([], Currencies.Ars, BankCodes.Generic, null, null);
        }
    }

    // ── Normalización de filas partidas (previa al parseo, solo ruta digital) ───

    /// <summary>
    /// Re-une filas que el agrupado por buckets fijos de Y partió en dos: los importes de una fila
    /// pueden renderizarse con una línea base ~3 pts distinta a la del texto (sistemático en la
    /// primera y última fila de cada página BBVA), y el redondeo Round(Y/3)*3 los manda a buckets
    /// distintos. Condiciones conservadoras: misma página, buckets adyacentes (|ΔY| ≤ 4.5), una
    /// fila con importes y solo caracteres sueltos del margen, la otra encabezada por fecha y sin
    /// ningún importe.
    /// </summary>
    private static List<StatementLine> MergeSplitAmountRows(List<StatementLine> rows)
    {
        var result = new List<StatementLine>(rows.Count);

        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var next = i + 1 < rows.Count ? rows[i + 1] : null;

            if (next != null && next.PageNumber == row.PageNumber && Math.Abs(next.Y - row.Y) <= 4.5)
            {
                // Caso observado: importes arriba, fecha+concepto abajo
                if (IsAmountsOnlyRow(row) && StartsWithDate(next) && !HasAmountCell(next))
                {
                    result.Add(new StatementLine(next.PageNumber, next.Y,
                        next.Cells.Concat(row.Cells).OrderBy(c => c.X).ToList()));
                    i++;
                    continue;
                }

                // Caso inverso por simetría: fecha+concepto arriba, importes abajo
                if (StartsWithDate(row) && !HasAmountCell(row) && IsAmountsOnlyRow(next))
                {
                    result.Add(new StatementLine(row.PageNumber, row.Y,
                        row.Cells.Concat(next.Cells).OrderBy(c => c.X).ToList()));
                    i++;
                    continue;
                }
            }

            result.Add(row);
        }

        return result;
    }

    private static bool HasAmountCell(StatementLine row) =>
        row.Cells.Any(c => IsArgentineAmount(c.Text));

    private static bool StartsWithDate(StatementLine row) =>
        row.Cells.Count > 0 &&
        RxDate.IsMatch(row.Cells[0].Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty);

    /// <summary>
    /// Fila compuesta por importes y, a lo sumo, caracteres sueltos del texto vertical de margen
    /// (BBVA imprime leyendas letra por letra en X≈577-585 que caen en cualquier bucket).
    /// </summary>
    private static bool IsAmountsOnlyRow(StatementLine row) =>
        row.Cells.Any(c => IsArgentineAmount(c.Text)) &&
        row.Cells.All(c => IsArgentineAmount(c.Text) || c.Text.Length <= 1);

    // ── Detección de moneda (a nivel documento, bank-agnóstica) ─────────────────

    /// <summary>Mensaje de rechazo cuando un mismo PDF contiene cuentas en más de una moneda.</summary>
    public const string MixedCurrencyError =
        "El extracto contiene cuentas en más de una moneda (pesos y dólares) en el mismo archivo. " +
        "Subí el extracto de cada cuenta por separado.";

    // Umbral de tokens de moneda (adyacentes a un importe) para inferir USD cuando el header
    // no es reconocible. >= 2 evita falsos positivos por una mención suelta en texto legal.
    private const int UsdTokenThreshold = 2;

    // Una celda que ES un marcador de moneda dólar: "USD", "-USD", "US$", "U$S" y también la
    // forma fusionada "USD607,62" que Galicia imprime en la columna de saldo.
    private static readonly Regex RxUsdTokenCell = new(
        @"^-?(?:USD|US\$|U\$S)", RegexOptions.Compiled);

    /// <summary>
    /// Detecta la moneda del extracto a nivel documento (una sola vez por PDF). Opera sobre las
    /// filas ya extraídas, así que funciona igual en la ruta digital y en la de OCR. Señales por
    /// confianza: (1) header de tipo de cuenta (pesos/dólares), (2) tokens dólar adyacentes a un
    /// importe con umbral, (3) fallback ARS. Headers de ambas monedas → <see cref="MixedCurrencyError"/>.
    /// </summary>
    private static string DetectCurrency(List<StatementLine> rows)
    {
        bool usdHeader = false;
        bool arsHeader = false;
        int  usdAmountTokens = 0;

        foreach (var row in rows)
        {
            var line = RemoveDiacritics(JoinCells(row)).ToUpperInvariant();

            if (line.Contains("ESPECIAL EN DOLARES") ||
                line.Contains("CORRIENTE EN DOLARES") ||
                line.Contains("CC U$S"))
                usdHeader = true;

            if (line.Contains("ESPECIAL EN PESOS") ||
                line.Contains("CORRIENTE EN PESOS") ||
                line.Contains("CC $"))
                arsHeader = true;

            // Tokens de moneda solo cuentan si están junto a un importe (columna de importe).
            for (int i = 0; i < row.Cells.Count; i++)
            {
                if (!RxUsdTokenCell.IsMatch(row.Cells[i].Text)) continue;

                bool nextIsAmount = i + 1 < row.Cells.Count && IsArgentineAmount(row.Cells[i + 1].Text);
                bool prevIsAmount = i - 1 >= 0             && IsArgentineAmount(row.Cells[i - 1].Text);
                if (nextIsAmount || prevIsAmount) usdAmountTokens++;
            }
        }

        return ResolveCurrency(usdHeader, arsHeader, usdAmountTokens);
    }

    /// <summary>
    /// Tabla de decisión de moneda, separada del escaneo para poder testearla sin un PDF. Headers
    /// de ambas monedas → excepción (extracto mixto). Un header reconocible manda sobre los tokens.
    /// Sin header, se infiere por cantidad de tokens dólar en columnas de importe.
    /// </summary>
    internal static string ResolveCurrency(bool usdHeader, bool arsHeader, int usdAmountTokens)
    {
        if (usdHeader && arsHeader)
            throw new InvalidOperationException(MixedCurrencyError);

        if (usdHeader) return Currencies.Usd;
        if (arsHeader) return Currencies.Ars;

        return usdAmountTokens >= UsdTokenThreshold ? Currencies.Usd : Currencies.Ars;
    }

    // ── Identificación de la cuenta (a nivel documento, bank-agnóstica) ─────────
    //
    // Se resuelve acá y no en cada estrategia de banco por el mismo motivo que la moneda: es un
    // dato del ENCABEZADO del documento, no de sus filas, y el criterio (buscar un número junto a
    // una etiqueta de cuenta, descartando CUIT/fechas/importes) es idéntico en todos los bancos.
    // Un banco nuevo hereda la detección sin escribir una línea.

    /// <summary>Solo el encabezado: pasada esa altura ya empiezan los movimientos.</summary>
    private const int HeaderLinesToScan = 40;

    /// <summary>CUIT: el falso positivo más común, porque convive con la cuenta en el encabezado.</summary>
    private static readonly Regex RxCuit = new(@"\b\d{2}-\d{8}-\d\b", RegexOptions.Compiled);

    // OJO con el nombre: esta clase hace `using static BankParsingHelpers`, que ya expone un
    // RxDate (dd/MM, sin año, el que usan las filas de movimientos). Un campo llamado igual acá lo
    // shadowearía y rompería StartsWithDate — y con él, el merge de filas partidas.
    private static readonly Regex RxFullDate = new(@"\b\d{1,2}[/\-]\d{1,2}[/\-]\d{2,4}\b", RegexOptions.Compiled);

    /// <summary>Importe con decimales: nunca es un número de cuenta.</summary>
    private static readonly Regex RxDecimalAmount = new(@"\d+,\d{2}\b", RegexOptions.Compiled);

    /// <summary>
    /// Corrida larga de dígitos con separadores internos, candidata a CBU/CVU. No incluye "/"
    /// a propósito: así una fecha corta la corrida en lugar de fusionarse con ella.
    /// </summary>
    private static readonly Regex RxLongDigitRun = new(@"\d[\d\s.\-]{18,34}\d", RegexOptions.Compiled);

    /// <summary>Cuenta agrupada: 123-456789/0, 3-029-0012345-6, 4210-9, 191-123-456789/0.</summary>
    private static readonly Regex RxGroupedAccount = new(@"\d{1,5}(?:[-/]\d{1,9}){1,3}", RegexOptions.Compiled);

    /// <summary>Cuenta escrita de corrido, sin separadores.</summary>
    private static readonly Regex RxPlainAccount = new(@"\b\d{7,20}\b", RegexOptions.Compiled);

    private static readonly string[] AccountLabels =
        ["CUENTA", "CTA", "CC ", "CA ", "NRO", "N°", "NUMERO"];

    /// <summary>
    /// Extrae del encabezado el número de cuenta y el CBU/CVU, normalizados a dígitos para poder
    /// compararlos contra <c>BankAccount.NormalizedNumber</c>. Devuelve <c>null</c> en lo que no
    /// pueda leer: es preferible no detectar nada —y que el usuario elija la cuenta a mano— antes
    /// que enrutar un extracto a la cuenta equivocada.
    /// </summary>
    internal static (string? AccountNumber, string? Cbu) DetectAccountIdentifiers(
        IReadOnlyList<StatementLine> rows)
    {
        string? cbu = null;
        string? account = null;

        foreach (var row in rows.Take(HeaderLinesToScan))
        {
            var line = RemoveDiacritics(JoinCells(row)).ToUpperInvariant();
            if (line.Length == 0) continue;

            // Los CUIT se borran antes de mirar nada más: 11 dígitos junto a una etiqueta de
            // cuenta son indistinguibles de un número de cuenta para cualquier heurística.
            var sanitized = RxCuit.Replace(line, " ");

            cbu     ??= FindCbu(sanitized);
            account ??= FindAccountNumber(sanitized);

            if (cbu is not null && account is not null) break;
        }

        return (account, cbu);
    }

    private static string? FindCbu(string line)
    {
        bool labeled = line.Contains("CBU") || line.Contains("CVU");

        foreach (Match m in RxLongDigitRun.Matches(line))
        {
            // Sin la etiqueta explícita solo se acepta una corrida contigua: una fila de totales
            // con varios importes separados por espacios puede sumar 22 dígitos por casualidad.
            if (!labeled && m.Value.Any(char.IsWhiteSpace)) continue;

            var digits = OnlyDigits(m.Value);
            if (digits.Length == 22) return digits;
        }

        return null;
    }

    private static string? FindAccountNumber(string line)
    {
        if (!AccountLabels.Any(line.Contains)) return null;

        // Fechas e importes se descartan antes de buscar: comparten forma con una cuenta.
        var cleaned = RxDecimalAmount.Replace(RxFullDate.Replace(line, " "), " ");

        // Primero el formato agrupado, que es el que usan los bancos para el número de cuenta.
        foreach (Match m in RxGroupedAccount.Matches(cleaned))
        {
            var digits = OnlyDigits(m.Value);
            if (digits.Length is >= 6 and <= 20) return digits;
        }

        foreach (Match m in RxPlainAccount.Matches(cleaned))
        {
            // Una corrida de 22 es el CBU, no el número corto de cuenta.
            if (m.Value.Length == 22) continue;
            return m.Value;
        }

        return null;
    }

    private static string OnlyDigits(string value) =>
        new([.. value.Where(char.IsDigit)]);
}
