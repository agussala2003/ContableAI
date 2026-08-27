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
/// Parser dedicado de extractos de Banco Ciudad. Formato propio: columnas
/// Fecha / Descripción / Referencia / Débitos / Créditos / Saldo, fechas dd/MM/yyyy e importes en
/// formato US (coma=miles, punto=decimal). Excluye la sección fiscal del total del banco.
/// </summary>
internal sealed class CiudadStatementParser : IBankStatementParser
{
    private readonly ILogger _logger;
    private const string BankCiudad = BankCodes.Ciudad;

    public CiudadStatementParser(ILogger? logger = null) => _logger = logger ?? NullLogger.Instance;

    public string Bank => BankCodes.Ciudad;

    public IReadOnlyList<BankTransaction> Parse(IReadOnlyList<StatementLine> lines, string? fileName)
        => ParseBancoCiudad([.. lines], _logger);

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
