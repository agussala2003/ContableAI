using ContableAI.Domain.Entities;
using ContableAI.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Globalization;
using System.Text.RegularExpressions;
using static ContableAI.Infrastructure.Services.BankParsingHelpers;

using ContableAI.Domain.Constants;

namespace ContableAI.Infrastructure.Services;

/// <summary>
/// Parser dedicado de extractos de Banco Ciudad. El banco entrega tres formatos distintos y se
/// elige por el encabezado de la tabla:
///   · Home banking "Últimos movimientos": Fecha / Monto (con signo) / N° de Comprobante /
///     Descripción / Saldo, fechas dd/MM/yyyy e importes argentinos ("$ -500.000,00").
///   · Resumen de cuenta oficial: FECHA / CONCEPTO / DÉBITO / CRÉDITO / SALDO / DESCRIPCIÓN DE
///     MOVIMIENTO, fechas "10-DIC-2025" e importes argentinos sin punto de millones ("1000.000,00").
///   · Formato legacy: Fecha / Descripción / Referencia / Débitos / Créditos / Saldo, importes en
///     formato US (coma=miles, punto=decimal). Excluye la sección fiscal del total del banco.
/// </summary>
internal sealed class CiudadStatementParser : IBankStatementParser
{
    private readonly ILogger _logger;
    private const string BankCiudad = BankCodes.Ciudad;

    public CiudadStatementParser(ILogger? logger = null) => _logger = logger ?? NullLogger.Instance;

    public string Bank => BankCodes.Ciudad;

    public IReadOnlyList<BankTransaction> Parse(IReadOnlyList<StatementLine> lines, string? fileName)
    {
        List<StatementLine> rows = [.. lines];

        if (rows.Any(IsHomeBankingHeader)) return ParseHomeBanking(rows, _logger);
        if (rows.Any(IsResumenHeader))     return ParseResumen(rows, _logger);

        return ParseBancoCiudad(rows, _logger);
    }

    /// <summary>
    /// Los dos formatos nuevos muestran la misma cuenta escrita distinto: el home banking como
    /// sucursal(8)+cuenta(10) de corrido ("000000290000016566") y el resumen como "0001656/6".
    /// La detección genérica lee el CUIT en el primero y "016566" en el segundo, lo que creaba dos
    /// cuentas provisionales para la misma cuenta real. Se normalizan ambos a la forma del
    /// resumen (8 dígitos, "00016566"), que es la que el usuario copia al cargar la cuenta.
    /// </summary>
    public string? DetectAccountNumber(IReadOnlyList<StatementLine> lines)
    {
        foreach (var row in lines.Take(40))
        {
            var line = RemoveDiacritics(JoinCells(row)).ToUpperInvariant();

            var hb = RxHomeBankingAccount.Match(line);
            if (hb.Success) return CanonicalAccount(hb.Groups[1].Value[^10..]);

            if (line.Contains("CUENTA NUMERO"))
            {
                var token = row.Cells.Select(c => c.Text).FirstOrDefault(t => RxResumenAccount.IsMatch(t));
                if (token is not null) return CanonicalAccount(new string([.. token.Where(char.IsDigit)]));
            }
        }

        return null;
    }

    private static string CanonicalAccount(string digits) => digits.TrimStart('0').PadLeft(8, '0');

    // "CC $ 000000290000016566" / "CA U$S ..." — tipo de cuenta, moneda y 18 dígitos.
    private static readonly Regex RxHomeBankingAccount = new(
        @"\b(?:CC|CA)\s+\S+\s+(\d{18})\b", RegexOptions.Compiled);

    // "0001656/6" — cuenta con dígito verificador, en la fila "CUENTA NÚMERO" del resumen.
    private static readonly Regex RxResumenAccount = new(@"^\d{4,12}/\d$", RegexOptions.Compiled);

    // Importe del resumen oficial: miles con punto pero sin punto de millones ("4653.730,89").
    private static readonly Regex RxResumenAmount = new(@"^-?\d+(?:\.\d{3})*,\d{2}$", RegexOptions.Compiled);

    private static readonly Regex RxResumenDate = new(@"^(\d{2})-([A-Z]{3})-(\d{4})$", RegexOptions.Compiled);

    private static readonly Dictionary<string, int> SpanishMonths = new()
    {
        ["ENE"] = 1, ["FEB"] = 2, ["MAR"] = 3, ["ABR"] = 4, ["MAY"] = 5, ["JUN"] = 6,
        ["JUL"] = 7, ["AGO"] = 8, ["SEP"] = 9, ["SET"] = 9, ["OCT"] = 10, ["NOV"] = 11, ["DIC"] = 12,
    };

    private static bool IsHomeBankingHeader(StatementLine row)
    {
        var u = RemoveDiacritics(JoinCells(row)).ToUpperInvariant();
        return u.Contains("FECHA") && u.Contains("MONTO") && u.Contains("COMPROBANTE");
    }

    private static bool IsResumenHeader(StatementLine row)
    {
        var u = RemoveDiacritics(JoinCells(row)).ToUpperInvariant();
        return u.Contains("FECHA") && u.Contains("CONCEPTO") && u.Contains("DEBITO") &&
               u.Contains("CREDITO") && u.Contains("SALDO");
    }

    // "N/D LEY 25413DEBIT", "LEY 25413DEBITOS": palabras pegadas en el PDF.
    private static string NormalizeDescription(IEnumerable<string> parts)
    {
        var desc = Regex.Replace(string.Join(" ", parts), @"\s+", " ").Trim();
        desc = Regex.Replace(desc, @"(LEY 25413)(?=[A-Z])", "$1 ", RegexOptions.IgnoreCase);
        return string.IsNullOrWhiteSpace(desc) ? "Movimiento Ciudad" : desc;
    }

    // ── Home banking "Últimos movimientos" ──────────────────────────────────────
    // Las columnas se corren de un export a otro (el ancho depende del contenido), así que se
    // lee por orden de tokens y no por posición: fecha, "$", monto con signo, comprobante,
    // descripción, "$", saldo. El monto es el primer importe y el saldo el último.
    private static List<BankTransaction> ParseHomeBanking(List<StatementLine> rows, ILogger logger)
    {
        var txs = new List<BankTransaction>();

        foreach (var row in rows)
        {
            if (row.Cells.Count == 0 ||
                !DateOnly.TryParseExact(row.Cells[0].Text.Trim(), "dd/MM/yyyy", out var date))
                continue;

            var texts = row.Cells.Select(c => c.Text.Trim().TrimStart('$').Trim()).ToList();
            var amountIdx = Enumerable.Range(1, texts.Count - 1).Where(i => IsArgentineAmount(texts[i])).ToList();
            if (amountIdx.Count < 2)
            {
                logger.LogDebug("[CIUDAD-HB] SKIP (sin monto y saldo) — '{Line}'", JoinCells(row));
                continue;
            }

            int montoIdx = amountIdx[0], saldoIdx = amountIdx[^1];
            var amount = ParseArgentineAmount(texts[montoIdx]);
            if (amount == 0) continue;

            int i = montoIdx + 1;
            string? comprobante = null;
            if (i < saldoIdx && Regex.IsMatch(texts[i], @"^\d+$")) comprobante = texts[i++];

            var descParts = texts[i..saldoIdx].Where(t => t.Length > 0);

            txs.Add(new BankTransaction
            {
                Date        = date,
                Description = NormalizeDescription(descParts),
                Amount      = Math.Abs(amount),
                Type        = amount < 0 ? TransactionType.Debit : TransactionType.Credit,
                SourceBank  = BankCiudad,
                ExternalId  = comprobante,
            });
        }

        logger.LogDebug("[CIUDAD-HB] Total transacciones parseadas: {Count}", txs.Count);
        return txs.OrderBy(t => t.Date).ToList();
    }

    // ── Resumen de cuenta oficial ───────────────────────────────────────────────
    // Columnas de importe alineadas a la derecha: se asignan por la posición del borde derecho
    // respecto de dónde arranca el encabezado de la columna siguiente.
    private static List<BankTransaction> ParseResumen(List<StatementLine> rows, ILogger logger)
    {
        var txs = new List<BankTransaction>();
        double? creditX = null, saldoX = null;

        foreach (var row in rows)
        {
            if (row.Cells.Count == 0) continue;

            if (IsResumenHeader(row))
            {
                foreach (var cell in row.Cells)
                {
                    var cu = RemoveDiacritics(cell.Text).ToUpperInvariant();
                    if (cu == "CREDITO")    creditX = cell.X;
                    else if (cu == "SALDO") saldoX  = cell.X;
                }
                continue;
            }

            if (creditX is null || saldoX is null) continue;

            var m = RxResumenDate.Match(row.Cells[0].Text.Trim().ToUpperInvariant());
            if (!m.Success || !SpanishMonths.TryGetValue(m.Groups[2].Value, out var month)) continue;

            var date = new DateOnly(int.Parse(m.Groups[3].Value), month, int.Parse(m.Groups[1].Value));

            decimal debit = 0, credit = 0;
            var descParts = new List<string>();

            foreach (var cell in row.Cells.Skip(1))
            {
                var txt = cell.Text.Trim();
                if (!RxResumenAmount.IsMatch(txt)) { descParts.Add(txt); continue; }

                if (cell.Right <= creditX)     debit  = ParseArgentineAmount(txt);
                else if (cell.Right <= saldoX) credit = ParseArgentineAmount(txt);
                // else: SALDO, no es movimiento
            }

            if (debit == 0 && credit == 0)
            {
                logger.LogDebug("[CIUDAD-RES] SKIP (sin importe) — '{Line}'", JoinCells(row));
                continue;
            }

            txs.Add(new BankTransaction
            {
                Date        = date,
                Description = NormalizeDescription(descParts),
                Amount      = debit > 0 ? debit : credit,
                Type        = debit > 0 ? TransactionType.Debit : TransactionType.Credit,
                SourceBank  = BankCiudad,
            });
        }

        logger.LogDebug("[CIUDAD-RES] Total transacciones parseadas: {Count}", txs.Count);
        return txs.OrderBy(t => t.Date).ToList();
    }

    // ── Lógica específica Banco Ciudad (movida verbatim desde PdfBankParser) ────
    private static List<BankTransaction> ParseBancoCiudad(List<StatementLine> rows, ILogger logger)
    {
        var txs = new List<BankTransaction>();
        bool inTable = false;

        // Valores por defecto medidos en el PDF de muestra (tolerancia = 80 pts)
        double rightDebit  = 432;
        double rightCredit = 503;
        double rightSaldo  = 572;
        double leftRef     = 302; // borde izquierdo de la columna REFERENCIA

        foreach (var row in rows)
        {
            if (row.Cells.Count == 0) continue;

            var lineText  = JoinCells(row);
            var upperLine = lineText.ToUpperInvariant();

            // Detectar encabezado de tabla (FECHA + DESCRIPCION + REFERENCIA)
            if (upperLine.Contains("FECHA") && upperLine.Contains("DESCRIPCION") && upperLine.Contains("REFERENCIA"))
            {
                inTable = true;
                foreach (var cell in row.Cells)
                {
                    var cu = cell.Text.ToUpperInvariant();
                    if (cu.Contains("DEBITO"))       rightDebit  = cell.Right;
                    else if (cu.Contains("CREDITO")) rightCredit = cell.Right;
                    else if (cu == "SALDO")          rightSaldo  = cell.Right;
                    else if (cu == "REFERENCIA")     leftRef     = cell.X;
                }
                logger.LogDebug("[CIUDAD] Header detectado — rightDebit={D} rightCredit={C} rightSaldo={S} leftRef={R}",
                    rightDebit, rightCredit, rightSaldo, leftRef);
                continue;
            }

            // "INT Y GSTOS BANCARIOS" es una sección separada que marca el fin del cuerpo de
            // transacciones — todo lo que sigue son cargos fiscales fuera del total DEBITOS/CREDITOS.
            // DEBITO FISCAL IVA e IMPUESTO A LOS DEBITOS aparecen como filas con fecha dentro de la
            // tabla; se omiten con continue (no break) para no cortar el parsing de fechas posteriores.
            if (inTable)
            {
                bool esSectionEnd = upperLine.Contains("INT Y GSTOS BANCARIOS") ||
                                    upperLine.Contains("INTERESES Y GASTOS BANCARIOS") ||
                                    upperLine.Contains("INTERES Y GASTOS BANCARIOS") ||
                                    upperLine.Contains("INTERES. GRALES") ||
                                    upperLine.Contains("GTOS. BANCARIOS") ||
                                    (upperLine.Contains("GSTOS") && upperLine.Contains("BANCARIO"));
                if (esSectionEnd)
                {
                    logger.LogDebug("[CIUDAD] BREAK — encabezado de sección fiscal: '{Line}'", lineText);
                    break;
                }

                bool esFilaFiscal = upperLine.Contains("DEBITO FISCAL IVA") ||
                                    upperLine.Contains("CREDITO FISCAL IVA") ||
                                    upperLine.Contains("IMPUESTO A LOS DEBITOS") ||
                                    upperLine.Contains("IMPUESTO A LOS DÉBITOS");
                if (esFilaFiscal)
                {
                    logger.LogDebug("[CIUDAD] SKIP (fila fiscal excluida del total banco): '{Line}'", lineText);
                    continue;
                }
            }

            if (!inTable) continue;

            // Filtro principal: la primera celda debe ser una fecha dd/MM/yyyy
            if (!DateOnly.TryParseExact(row.Cells[0].Text.Trim(), "dd/MM/yyyy", out var date))
            {
                logger.LogDebug("[CIUDAD] SKIP (sin fecha) — '{Line}'", lineText);
                continue;
            }

            // Saltar filas de saldo (INICIAL, ANTERIOR, FINAL) — son balances, no transacciones.
            if (upperLine.Contains("SALDO FINAL") ||
                upperLine.Contains("SALDO INICIAL") ||
                upperLine.Contains("SALDO ANTERIOR"))
            {
                logger.LogDebug("[CIUDAD] SKIP (saldo) — '{Line}'", lineText);
                continue;
            }

            logger.LogDebug("[CIUDAD] Fila en tabla con fecha {Date} — '{Line}'", date, lineText);

            decimal debitAmt = 0, creditAmt = 0;
            var descParts = new List<string>();
            string? referencia = null;

            foreach (var cell in row.Cells.Skip(1))
            {
                var txt = cell.Text.Trim();
                if (string.IsNullOrWhiteSpace(txt)) continue;

                if (IsCiudadAmount(txt))
                {
                    var rawAmt = ParseCiudadAmount(txt);
                    // Negative amounts only appear in the SALDO column (running balance).
                    // DEBITOS and CREDITOS are always positive — a negative value here
                    // means the column classifier would misidentify SALDO INICIAL/ANTERIOR
                    // as a debit. Skip it unconditionally.
                    if (rawAmt < 0)
                    {
                        logger.LogDebug("[CIUDAD]   importe NEGATIVO {Amt} en cell.Right={R} — ignorado (saldo)", rawAmt, cell.Right);
                        continue;
                    }

                    var amt = rawAmt;
                    double distDebit  = Math.Abs(cell.Right - rightDebit);
                    double distCredit = Math.Abs(cell.Right - rightCredit);
                    double distSaldo  = Math.Abs(cell.Right - rightSaldo);
                    double minDist    = Math.Min(distDebit, Math.Min(distCredit, distSaldo));

                    if (minDist > 80) { descParts.Add(txt); continue; }

                    if (distSaldo == minDist)
                    {
                        logger.LogDebug("[CIUDAD]   importe {Amt} → SALDO (ignorado, dS={DS:F0} cell.Right={R:F0})", amt, distSaldo, cell.Right);
                        continue;
                    }
                    if (distDebit == minDist) { debitAmt  = amt; logger.LogDebug("[CIUDAD]   importe {Amt} → DEBITO  (dD={DD:F0} cell.Right={R:F0})", amt, distDebit,  cell.Right); }
                    else                      { creditAmt = amt; logger.LogDebug("[CIUDAD]   importe {Amt} → CREDITO (dC={DC:F0} cell.Right={R:F0})", amt, distCredit, cell.Right); }
                }
                else if (cell.X >= leftRef - 15 && cell.X < rightDebit - 20)
                {
                    // Columna REFERENCIA: sólo números útiles (≠ "0") → ExternalId
                    if (Regex.IsMatch(txt, @"^\d+$") && txt != "0")
                        referencia = txt;
                }
                else if (cell.X < leftRef - 5)
                {
                    // Columna DESCRIPCION
                    descParts.Add(txt);
                }
                // else: zona entre REFERENCIA y DEBITOS → ignorar
            }

            if (debitAmt == 0 && creditAmt == 0)
            {
                logger.LogDebug("[CIUDAD] SKIP (sin importe) — {Date} '{Line}'", date, lineText);
                continue;
            }

            var rawDesc = Regex.Replace(string.Join(" ", descParts), @"\s+", " ").Trim();

            // Artefacto PDF: "25413CREDITOS" o "25413DEBITOS" (palabras pegadas sin espacio)
            rawDesc = Regex.Replace(rawDesc, @"25413(CREDITOS|DEBITOS)", "25413 $1", RegexOptions.IgnoreCase);

            if (string.IsNullOrWhiteSpace(rawDesc)) rawDesc = "Movimiento Ciudad";

            var txType = debitAmt > 0 ? TransactionType.Debit : TransactionType.Credit;
            var txAmt  = debitAmt > 0 ? debitAmt : creditAmt;
            logger.LogDebug("[CIUDAD] TX — {Date} | {Type} | {Amount} | '{Desc}'", date, txType, txAmt, rawDesc);

            txs.Add(new BankTransaction
            {
                Date        = date,
                Description = rawDesc,
                Amount      = txAmt,
                Type        = txType,
                SourceBank  = BankCiudad,
                ExternalId  = referencia,
            });
        }

        logger.LogDebug("[CIUDAD] Total transacciones parseadas: {Count}", txs.Count);
        return txs.OrderBy(t => t.Date).ToList();
    }

    private static bool IsCiudadAmount(string text) =>
        !string.IsNullOrWhiteSpace(text) && RxCiudadAmount.IsMatch(text.Trim());

    private static decimal ParseCiudadAmount(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0;
        var clean = raw.Trim().Replace(",", ""); // quitar separador de miles (coma)
        return decimal.TryParse(clean, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }
}
