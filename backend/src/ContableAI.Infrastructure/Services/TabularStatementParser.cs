using ContableAI.Domain.Entities;
using ContableAI.Domain.Enums;
using System.Text.RegularExpressions;
using static ContableAI.Infrastructure.Services.BankParsingHelpers;

namespace ContableAI.Infrastructure.Services;

/// <summary>
/// Motor compartido (Template Method) para bancos cuyo extracto es una tabla con columnas
/// Fecha / Descripción / Débito / Crédito / Saldo (BBVA, Galicia, Credicoop y el genérico).
/// Recorre las filas manteniendo estado (dentro/fuera de la tabla), mapea las columnas por
/// posición X y clasifica cada importe. El comportamiento específico de cada banco se inyecta
/// mediante hooks virtuales; las subclases sobreescriben solo lo que difiere.
/// </summary>
internal abstract class TabularStatementParser : IBankStatementParser
{
    public abstract string Bank { get; }

    /// <summary>Una transacción a emitir desde una fila (importe + tipo + descripción).</summary>
    protected readonly record struct Emission(decimal Amount, TransactionType Type, string Description);

    // ── Hooks por banco ────────────────────────────────────────────────────────

    /// <summary>Credicoop: las tablas anexas no traen columna SALDO y deben ignorarse.</summary>
    protected virtual bool IsAccessoryTableHeader(bool hasSaldoHeader) => false;

    /// <summary>Galicia: recupera la X de la columna crédito cuando el mapeo estándar falla.</summary>
    protected virtual double RefineCreditColumn(StatementLine headerRow, string upperLine, double rightCredit) => rightCredit;

    /// <summary>
    /// Convierte los importes clasificados de una fila en transacciones. Por defecto una sola
    /// (débito si hay débito, si no crédito). BBVA la sobreescribe para desdoblar filas fusionadas.
    /// </summary>
    protected virtual List<Emission> BuildEmissions(List<decimal> debitAmts, List<decimal> creditAmts, string rawDesc)
    {
        var dAmt = debitAmts.Count > 0 ? debitAmts[0] : 0m;
        var cAmt = creditAmts.Count > 0 ? creditAmts[0] : 0m;
        return [new Emission(dAmt > 0 ? dAmt : cAmt, dAmt > 0 ? TransactionType.Debit : TransactionType.Credit, rawDesc)];
    }

    /// <summary>Extrae un id externo de la descripción (y la limpia). Galicia/Credicoop/BBVA.</summary>
    protected virtual (string desc, string? extId) ExtractExternalId(string desc) => (desc, null);

    /// <summary>Galicia: una fila sin fecha con importes es la fila de totales, no un movimiento.</summary>
    protected virtual bool IsSummaryAmountRow(string rawDesc, int debitCount, int creditCount) => false;

    /// <summary>Galicia: texto de continuación que en realidad es un resumen (no descripción).</summary>
    protected virtual bool IsSummaryText(string extra) => false;

    /// <summary>Galicia/Credicoop: reextrae el id externo tras fusionar una fila de continuación.</summary>
    protected virtual (string desc, string? extId) MergeContinuationExternalId(string newDesc, string? currentExtId) => (newDesc, currentExtId);

    /// <summary>Post-proceso específico (BBVA: enriquecimiento con anexos de cheques/transferencias).</summary>
    protected virtual void PostProcess(List<BankTransaction> txs, List<StatementLine> rows, int? stmtYear, int? primaryMonth) { }

    public IReadOnlyList<BankTransaction> Parse(IReadOnlyList<StatementLine> lines, string? fileName)
        => ParseStateful([.. lines], fileName);

    // ── Motor (adaptado desde el antiguo ParseStateful/ProcessNewTransactionRow/ProcessContinuationRow) ──
    private List<BankTransaction> ParseStateful(List<StatementLine> rows, string? fileName = null)
    {
        var txs = new List<BankTransaction>();
        BankTransaction? currentTx = null;

        bool inTable = false;
        bool pastMainTable = false; // Evita que un nuevo encabezado reactive la tabla después del resumen final.

        // Pre-escaneo: detecta el año y mes principal del extracto a partir de fechas explícitas
        var (stmtYear, primaryMonth) = DetectStatementInfo(rows, fileName);

        double colDescStart = -1, colDescEnd = -1;
        double rightDebit = -1, rightCredit = -1, rightSaldo = -1, leftSaldo = -1;

        foreach (var row in rows)
        {
            var lineText = JoinCells(row);
            if (string.IsNullOrWhiteSpace(lineText)) continue;

            var upperLine = lineText.ToUpperInvariant();

            // Cortacorrientes para ignorar resúmenes finales, anexos legales y cartas formales
            if (IsEndOfTableMarker(upperLine)) 
            {
                inTable = false;
                
                if (upperLine.StartsWith("SALDO AL") ||
                    upperLine.Contains("TOTAL MOVIMIENTOS") ||
                    upperLine.Contains("NRO DE CHEQUE") ||
                    IsGaliciaRetentionSummaryRow(upperLine) ||
                    upperLine.Contains("EL CREDITO DE IMPUESTO") ||
                    upperLine.Contains("IMPUESTO A LOS DEBITOS") ||
                    upperLine.Contains("IMPUESTO A LOS DÉBITOS") ||
                    upperLine.Contains("REGIMEN SISTEMA SIRCREB") ||
                    upperLine.Contains("RÉGIMEN SISTEMA SIRCREB"))
                {
                    pastMainTable = true;
                }
                continue;
            }

            if (IsIrrelevantLine(upperLine) || upperLine == "SIN MOVIMIENTOS") continue;

            // Detección de encabezados de tabla
            bool isHeaderFecha = upperLine.Contains("FECHA") || upperLine.Contains("F.");
            bool isHeaderMov   = upperLine.Contains("DEBITO") || upperLine.Contains("DÉBITO") || upperLine.Contains("DEBE") ||
                                 upperLine.Contains("CREDITO") || upperLine.Contains("CRÉDITO") || upperLine.Contains("HABER") ||
                                 upperLine.Contains("IMPORTE");
            bool isHeaderSaldo = upperLine.Contains("SALDO") || upperLine.Contains("SALDOS");

            if (isHeaderFecha && isHeaderMov)
            {
                // Tablas anexas sin columna SALDO (Credicoop): no son la tabla principal.
                if (IsAccessoryTableHeader(isHeaderSaldo))
                {
                    inTable = false;
                    continue;
                }

                // Ignora tablas accesorias (ej. Transferencias) si ya pasamos la tabla principal
                if (pastMainTable)
                {
                    inTable = false;
                    continue;
                }

                inTable = true;
                
                // Mapeo posicional de las columnas para alinear los números de las filas siguientes
                foreach (var cell in row.Cells)
                {
                    var cUpper = cell.Text.ToUpperInvariant();
                    
                    if (cUpper.Contains("DESC") || cUpper.Contains("CONCEPTO") || cUpper.Contains("DETALLE") || cUpper.Contains("ORIGEN") || cUpper.Contains("COMBTE"))
                        if (colDescStart < 0) colDescStart = cell.X;
                        
                    if (cUpper.Contains("DEBITO") || cUpper.Contains("DÉBITO") || cUpper.Contains("DEBE"))       
                        rightDebit = cell.Right;
                        
                    if (cUpper.Contains("CREDITO") || cUpper.Contains("CRÉDITO") || cUpper.Contains("HABER") || cUpper.Contains("IMPORTE")) 
                        rightCredit = cell.Right;
                        
                    if (cUpper == "SALDO" || cUpper == "SALDOS") 
                    { 
                        rightSaldo = cell.Right; 
                        leftSaldo = cell.X; 
                    }
                }

                rightCredit = RefineCreditColumn(row, upperLine, rightCredit);

                // Definir límites de la columna de descripción
                if (colDescStart > 0)
                {
                    var headerCells = row.Cells.Where(c => 
                        c.Text.ToUpperInvariant().Contains("DEB") || 
                        c.Text.ToUpperInvariant().Contains("DÉB") || 
                        c.Text.ToUpperInvariant().Contains("CRED") || 
                        c.Text.ToUpperInvariant().Contains("CRÉD") || 
                        c.Text.ToUpperInvariant().Contains("HABER") || 
                        c.Text.ToUpperInvariant().Contains("IMPORTE") || 
                        c.Text.ToUpperInvariant().Contains("SALDO")).ToList();
                        
                    colDescEnd = headerCells.Any() ? headerCells.Min(c => c.X) - 15 : colDescStart + 250;
                }
                
                continue; 
            }

            if (inTable)
            {
                // Si la primera celda es una fecha, es el inicio de una transacción nueva
                if (row.Cells.Count > 0 && TryParseDate(row.Cells[0].Text, out var date, stmtYear, primaryMonth))
                {
                    ProcessNewTransactionRow(row, rightDebit, rightCredit, rightSaldo, leftSaldo, date, lineText, txs, ref currentTx);
                    
                    // Si la fila contenía el balance final, es el fin de la tabla.
                    if (upperLine.Contains("SALDO AL ") || upperLine.EndsWith("SALDO AL"))
                    {
                        inTable = false;
                        pastMainTable = true;
                    }
                }
                // Si no hay fecha, pero venimos de una transacción, puede ser una continuación de la descripción o una fila fusionada (BBVA)
                else if (currentTx != null)
                {
                    ProcessContinuationRow(row, colDescStart, colDescEnd, rightDebit, rightCredit, rightSaldo, txs, ref currentTx);
                }
            }
        }

        // Post-proceso específico del banco (BBVA enriquece con anexos de cheques/transferencias).
        PostProcess(txs, rows, stmtYear, primaryMonth);

        return txs.OrderBy(t => t.Date).ToList();
    }

    private void ProcessNewTransactionRow(StatementLine row, double rightDebit, double rightCredit, double rightSaldo, double leftSaldo, DateOnly date, string lineText, List<BankTransaction> txs, ref BankTransaction? currentTx)
    {
        var debitAmts  = new List<decimal>(2);
        var creditAmts = new List<decimal>(2);
        var descParts  = new List<string>();

        foreach (var cell in row.Cells.Skip(1))
        {
            if (IsArgentineAmount(cell.Text))
            {
                var amt = Math.Abs(ParseArgentineAmount(cell.Text));
                double cellRight = cell.Right;

                double distDebit  = rightDebit > 0 ? Math.Abs(cellRight - rightDebit) : 9999;
                double distCredit = rightCredit > 0 ? Math.Abs(cellRight - rightCredit) : 9999;
                double distSaldo  = rightSaldo > 0 ? Math.Abs(cellRight - rightSaldo) : 9999;

                double minDist = Math.Min(distDebit, Math.Min(distCredit, distSaldo));

                // Tolerancia de 60 puntos para diferencias de alineación entre el encabezado y el número impreso
                if (minDist > 60)                
                    descParts.Add(cell.Text);
                else if (minDist == distSaldo)   
                    continue;
                else if (minDist == distDebit)   
                    debitAmts.Add(amt);
                else if (minDist == distCredit)  
                    creditAmts.Add(amt);
            }
            else
            {
                // Excluir celdas de texto que caigan dentro de la columna SALDO.
                // Usar el borde izquierdo del encabezado SALDO como límite derecho de la descripción.
                double descLimit = leftSaldo > 0 
                    ? leftSaldo 
                    : Math.Max(rightDebit > 0 ? rightDebit : 0, rightCredit > 0 ? rightCredit : 0);
                    
                if (descLimit > 0 && cell.X >= descLimit - 5) 
                    continue;
                    
                descParts.Add(cell.Text);
            }
        }

        // Fallback si la detección posicional falló
        if (debitAmts.Count == 0 && creditAmts.Count == 0)
        {
            var amounts = ExtractAmountsFromText(lineText);
            if (amounts.Count > 0)
            {
                var a = amounts[0];
                if (a < 0) debitAmts.Add(Math.Abs(a));
                else       creditAmts.Add(Math.Abs(a));
            }
        }

        if (debitAmts.Count > 0 || creditAmts.Count > 0)
        {
            var rawDesc = Regex.Replace(string.Join(" ", descParts).Trim(), @"\s+", " ");
            
            // Strip trailing "SALDO AL ..." que a veces aparece en la última fila de la tabla
            var si = rawDesc.IndexOf("SALDO AL", StringComparison.OrdinalIgnoreCase); 
            if (si >= 0) 
                rawDesc = rawDesc[..si].TrimEnd(); 
                
            // La conversión fila → transacciones la decide el banco (BBVA desdobla filas fusionadas;
            // el resto emite una sola). Luego cada emisión pasa por la extracción de id externo.
            foreach (var emission in BuildEmissions(debitAmts, creditAmts, rawDesc))
            {
                var (cleanDesc, extId) = ExtractExternalId(emission.Description);

                currentTx = new BankTransaction
                {
                    Date        = date,
                    Description = cleanDesc,
                    Amount      = emission.Amount,
                    Type        = emission.Type,
                    SourceBank  = Bank,
                    ExternalId  = extId,
                };
                txs.Add(currentTx);
            }
        }
    }

    private void ProcessContinuationRow(StatementLine row, double colDescStart, double colDescEnd, double rightDebit, double rightCredit, double rightSaldo, List<BankTransaction> txs, ref BankTransaction? currentTx)
    {
        var contDebitAmts  = new List<decimal>();
        var contCreditAmts = new List<decimal>();
        var allTextParts   = new List<string>();
        var descOnlyParts  = new List<string>();

        foreach (var cell in row.Cells)
        {
            if (IsArgentineAmount(cell.Text))
            {
                double cellRight = cell.Right;
                double distDebit  = rightDebit  > 0 ? Math.Abs(cellRight - rightDebit)  : 9999;
                double distCredit = rightCredit > 0 ? Math.Abs(cellRight - rightCredit) : 9999;
                double distSaldo  = rightSaldo  > 0 ? Math.Abs(cellRight - rightSaldo)  : 9999;
                
                double minDist = Math.Min(distDebit, Math.Min(distCredit, distSaldo));

                // Ignorar si cae en la columna de saldo o está fuera de foco
                if (minDist > 60 || minDist == distSaldo) continue;
                
                if (minDist == distDebit)  
                    contDebitAmts.Add(Math.Abs(ParseArgentineAmount(cell.Text)));
                else                       
                    contCreditAmts.Add(Math.Abs(ParseArgentineAmount(cell.Text)));
            }
            else
            {
                allTextParts.Add(cell.Text);
                
                if (colDescStart > 0)
                {
                    if (cell.X >= colDescStart - 30 && cell.X <= (colDescEnd > 0 ? colDescEnd + 30 : 9999))
                    {
                        descOnlyParts.Add(cell.Text);
                    }
                }
                else
                {
                    descOnlyParts.Add(cell.Text);
                }
            }
        }

        // Si hay importes, es una transacción independiente donde el banco omitió la fecha (típico BBVA)
        if (contDebitAmts.Count > 0 || contCreditAmts.Count > 0)
        {
            var rawContDesc = Regex.Replace(string.Join(" ", allTextParts).Trim(), @"\s+", " ");
            if (IsSummaryAmountRow(rawContDesc, contDebitAmts.Count, contCreditAmts.Count))
                return;
            var dAmt = contDebitAmts.Count > 0 ? contDebitAmts[0] : 0m;
            var cAmt = contCreditAmts.Count > 0 ? contCreditAmts[0] : 0m;
            var contType = dAmt > 0 ? TransactionType.Debit : TransactionType.Credit;
            var contAmt  = dAmt > 0 ? dAmt : cAmt;

            var (contDesc, contExtId) = ExtractExternalId(rawContDesc);

            currentTx = new BankTransaction
            {
                Date        = currentTx!.Date, // Hereda la fecha de la última TX registrada
                Description = contDesc,
                Amount      = contAmt,
                Type        = contType,
                SourceBank  = Bank,
                ExternalId  = contExtId,
            };
            txs.Add(currentTx);
        }
        // Si no hay importes, es texto que continúa la descripción anterior
        else if (descOnlyParts.Count > 0)
        {
            var extra = string.Join(" ", descOnlyParts).Trim();

            // Texto de continuación que en realidad es un fragmento de resumen (Galicia).
            if (IsSummaryText(extra))
                return;

            if (!string.IsNullOrWhiteSpace(extra) && !IsIrrelevantLine(extra))
            {
                var newDesc = Regex.Replace(currentTx!.Description + " " + extra, @"\s+", " ").Trim();
                string? mergedExtId = currentTx.ExternalId;

                var (cleanedDesc, reExtId) = MergeContinuationExternalId(newDesc, mergedExtId);
                mergedExtId = reExtId;

                currentTx = new BankTransaction
                {
                    Date        = currentTx.Date,
                    Description = cleanedDesc,
                    Amount      = currentTx.Amount,
                    Type        = currentTx.Type,
                    SourceBank  = currentTx.SourceBank,
                    ExternalId  = mergedExtId,
                };
                txs[^1] = currentTx; // Actualiza el último elemento
            }
        }
    }
}
