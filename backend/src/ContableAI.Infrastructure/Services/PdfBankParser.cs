using ContableAI.Domain.Entities;
using ContableAI.Domain.Enums;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace ContableAI.Infrastructure.Services;

/// <summary>
/// Extrae transacciones bancarias desde extractos PDF usando PdfPig con análisis posicional avanzado.
/// Soporta descripciones multilínea, detección automática de columnas y casos específicos por banco.
/// </summary>
public class PdfBankParser : IBankParser
{
    #region Constantes y Expresiones Regulares

    public string BankCode    => "PDF";
    public string DisplayName => "PDF (extracto bancario)";

    // Detecta fechas en formato dd/mm, dd-mm, dd/mm/yy o dd/mm/yyyy
    private static readonly Regex RxDate = new(
        @"^(\d{1,2})[/\-](\d{1,2})(?:[/\-](\d{2,4}))?$",
        RegexOptions.Compiled);

    // Detecta importes estándar argentinos (ej. 1.234,56 o -1.234,56)
    private static readonly Regex RxAmount = new(
        @"^-?[\d]{1,3}(?:\.[\d]{3})*,\d{2}$",
        RegexOptions.Compiled);

    // Detecta importes cortos sin separador de miles (ej. 1234,56)
    private static readonly Regex RxAmountShort = new(
        @"^-?\d+,\d{2}$",
        RegexOptions.Compiled);

    private const string BankBbva        = "BBVA";
    private const string BankGalicia     = "GALICIA";
    private const string BankCredicoop   = "CREDICOOP";
    private const string BankMercadoPago = "MERCADOPAGO";

    #endregion

    #region Clases de Soporte Internas

    private sealed record PdfRow(int PageNumber, double Y, List<PdfCell> Cells);
    private sealed record PdfCell(double X, double Right, string Text);
    private sealed record BbvaChequeData(DateOnly Emision, DateOnly Pago);
    private sealed record TxAnchor(int PageNumber, double RowY, DateOnly Date, decimal Amount, TransactionType Type);

    #endregion

    #region API Pública

    public IEnumerable<BankTransaction> Parse(Stream stream, string fileName)
    {
        var ms = new MemoryStream();
        stream.CopyTo(ms);
        ms.Position = 0;

        try
        {
            using var doc = PdfDocument.Open(ms.ToArray());
            var pages = doc.GetPages().ToList();

            var bank = DetectBank(pages, fileName);
            var rows = ExtractPositionalRows(pages);

            return bank switch
            {
                BankMercadoPago => ParseMercadoPago(rows),
                _               => ParseStateful(rows, bank)
            };
        }
        catch
        {
            return Enumerable.Empty<BankTransaction>();
        }
    }

    #endregion

    #region Detección y Extracción Base

    /// <summary>
    /// Determina a qué banco pertenece el PDF analizando el nombre del archivo 
    /// y una muestra del texto de las primeras páginas.
    /// </summary>
    private static string DetectBank(IList<Page> pages, string fileName)
    {
        var normalizedFileName = (fileName ?? string.Empty).ToUpperInvariant()
            .Replace("_20", " ")
            .Replace("%20", " ");
        
        if (normalizedFileName.Contains("BBVA"))        return BankBbva;
        if (normalizedFileName.Contains("GALICIA"))     return BankGalicia;
        if (normalizedFileName.Contains("CREDICOOP"))   return BankCredicoop;
        
        if (normalizedFileName.Contains("MERCADOPAGO") || 
            normalizedFileName.Contains("_MP_") || 
            normalizedFileName.Contains(" MP ") || 
            normalizedFileName.Contains("-MP-"))        return BankMercadoPago;

        // Fallback: Analizar el contenido de las primeras dos páginas
        var sample = new StringBuilder();
        foreach (var page in pages.Take(2))
        {
            foreach (var word in page.GetWords())
            {
                sample.Append(word.Text).Append(' ');
            }
        }

        var text = sample.ToString().ToUpperInvariant();

        if (text.Contains("BBVA") || text.Contains("CREANDO OPORTUNIDADES") || text.Contains("CBU 0170")) 
            return BankBbva;
            
        if (text.Contains("GALICIA")) 
            return BankGalicia;
            
        if (text.Contains("CREDICOOP") || text.Contains("COOPERATIVA DE CRED")) 
            return BankCredicoop;
            
        if (text.Contains("MERCADO PAGO") || text.Contains("MERCADOPAGO") || text.Contains("00000031") || text.Contains("MERCADO LIBRE")) 
            return BankMercadoPago;

        return "GENERIC";
    }

    /// <summary>
    /// Agrupa las palabras del PDF en filas (PdfRow) basándose en su coordenada Y.
    /// Utiliza una tolerancia de 3 puntos para unificar palabras que visualmente 
    /// están en la misma línea pero difieren por pequeños decimales en su renderizado.
    /// </summary>
    private static List<PdfRow> ExtractPositionalRows(IEnumerable<Page> pages)
    {
        var rows = new List<PdfRow>();
        
        foreach (var page in pages)
        {
            var words = page.GetWords().ToList();
            if (words.Count == 0) continue;

            // Agrupa aplicando la tolerancia de 3.0
            var byY = words
                .GroupBy(w => Math.Round(w.BoundingBox.Bottom / 3.0) * 3.0)
                .OrderByDescending(g => g.Key);

            foreach (var group in byY)
            {
                var cells = group
                    .OrderBy(w => w.BoundingBox.Left)
                    .Select(w => new PdfCell(w.BoundingBox.Left, w.BoundingBox.Right, w.Text.Trim()))
                    .Where(c => !string.IsNullOrWhiteSpace(c.Text))
                    .ToList();

                if (cells.Count > 0)
                {
                    rows.Add(new PdfRow(page.Number, group.Key, cells));
                }
            }
        }
        
        return rows;
    }

    #endregion

    #region Lógica de Parsing Estatal (Bancos Tradicionales)

    /// <summary>
    /// Procesa línea por línea identificando encabezados, columnas y transacciones 
    /// para bancos tradicionales (BBVA, Galicia, Credicoop).
    /// Mantiene un estado (`inTable`) para saber si está leyendo transacciones útiles.
    /// </summary>
    private static List<BankTransaction> ParseStateful(List<PdfRow> rows, string bankCode)
    {
        var txs = new List<BankTransaction>();
        BankTransaction? currentTx = null;
        
        bool inTable = false;
        bool pastMainTable = false; // Evita que un nuevo encabezado reactive la tabla después del resumen final.

        // Pre-escaneo: detecta el año del extracto a partir de alguna fecha explícita
        int? stmtYear = DetectStatementYear(rows);

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
                // Parche Credicoop: La tabla principal siempre tiene la columna SALDO. Las anexas no.
                if (bankCode == BankCredicoop && !isHeaderSaldo)
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

                if (bankCode == BankGalicia && rightCredit < 0 && upperLine.Contains("CRÉDITO"))
                {
                    var cCell = row.Cells.FirstOrDefault(c => c.Text.ToUpperInvariant().Contains("CRÉDITO") || c.Text.ToUpperInvariant().Contains("CREDITO"));
                    if (cCell != null) rightCredit = cCell.Right;
                }

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
                if (row.Cells.Count > 0 && TryParseDate(row.Cells[0].Text, out var date, stmtYear))
                {
                    ProcessNewTransactionRow(row, bankCode, rightDebit, rightCredit, rightSaldo, leftSaldo, date, lineText, txs, ref currentTx);
                    
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
                    ProcessContinuationRow(row, bankCode, colDescStart, colDescEnd, rightDebit, rightCredit, rightSaldo, txs, ref currentTx);
                }
            }
        }

        // ── Enriquecimiento específico para BBVA usando anexos ──────────────
        if (bankCode == BankBbva && txs.Count > 0)
        {
            // Limpieza de descripciones y desambiguación ANTES del enriquecimiento
            LinkAndDisambiguateBbva(txs);

            var (debitosAuto, chequesMap, transfersIn, transfersOut) = ParseBbvaSupplementaryData(rows, stmtYear);
            ApplyBbvaEnrichments(txs, debitosAuto, chequesMap, transfersIn, transfersOut);
        }

        return txs.OrderBy(t => t.Date).ToList();
    }

    private static bool IsEndOfTableMarker(string upperLine)
    {
        return upperLine.StartsWith("SALDO AL") || 
               upperLine.Contains("DEBITOS AUTOMATICOS DEL") || 
               upperLine.Contains("CABAL DEBITO DEL") || 
               upperLine.Contains("TRANSFERENCIAS PESOS DEL") ||
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
    private static bool IsGaliciaRetentionSummaryRow(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;

        var normalized = RemoveDiacritics(line).ToUpperInvariant();
        var compact = new string(normalized.Where(char.IsLetterOrDigit).ToArray());
        return compact.Contains("TOTAL") && compact.Contains("RETENCI") && compact.Contains("IMPUEST");
    }

    private static bool IsGaliciaRetentionSummaryText(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;

        var normalized = RemoveDiacritics(line).ToUpperInvariant();
        var compact = new string(normalized.Where(char.IsLetterOrDigit).ToArray());
        return compact.Contains("RETENCI") && compact.Contains("IMPUEST");
    }

    private static bool IsGaliciaSplitSummaryAmountRow(string rawDesc, int debitCount, int creditCount)
    {
        if (debitCount == 0 || creditCount == 0) return false;

        var normalized = RemoveDiacritics(rawDesc).ToUpperInvariant();
        var letters = new string(normalized.Where(char.IsLetter).ToArray());

        // Amount-only rows like "$ -$ $" (or with just "TOTAL") are summary fragments, not transactions.
        return letters.Length == 0 || letters == "TOTAL";
    }

    private static string RemoveDiacritics(string text)
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

    private static void ProcessNewTransactionRow(PdfRow row, string bankCode, double rightDebit, double rightCredit, double rightSaldo, double leftSaldo, DateOnly date, string lineText, List<BankTransaction> txs, ref BankTransaction? currentTx)
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
                
            int totalAmts = debitAmts.Count + creditAmts.Count;

            // En BBVA, a veces dos transacciones se fusionan en una sola línea del PDF.
            var descSegments = (totalAmts >= 2 && bankCode == BankBbva)
                ? SplitMergedBbvaDescription(rawDesc)
                : [rawDesc];

            var toEmit = new List<(decimal Amt, TransactionType TxType, string Desc)>(totalAmts);
            var pendingDebits  = new Queue<decimal>(debitAmts);
            var pendingCredits = new Queue<decimal>(creditAmts);

            if (totalAmts >= 2 && bankCode == BankBbva)
            {
                foreach (var seg in descSegments)
                {
                    bool isReversal = Regex.IsMatch(seg, @"^\s*C\s");
                    
                    if (isReversal && pendingDebits.Count > 0)           
                        toEmit.Add((pendingDebits.Dequeue(), TransactionType.Debit, seg));
                    else if (!isReversal && pendingCredits.Count > 0)    
                        toEmit.Add((pendingCredits.Dequeue(), TransactionType.Credit, seg));
                    else if (pendingDebits.Count > 0)                    
                        toEmit.Add((pendingDebits.Dequeue(), TransactionType.Debit, seg));
                    else if (pendingCredits.Count > 0)                   
                        toEmit.Add((pendingCredits.Dequeue(), TransactionType.Credit, seg));
                }
                
                var lastSeg = descSegments.Length > 0 ? descSegments[^1] : rawDesc;
                while (pendingDebits.Count > 0)  
                    toEmit.Add((pendingDebits.Dequeue(), TransactionType.Debit, lastSeg));
                while (pendingCredits.Count > 0) 
                    toEmit.Add((pendingCredits.Dequeue(), TransactionType.Credit, lastSeg));
            }
            else
            {
                var dAmt = debitAmts.Count > 0 ? debitAmts[0] : 0m;
                var cAmt = creditAmts.Count > 0 ? creditAmts[0] : 0m;
                toEmit.Add((dAmt > 0 ? dAmt : cAmt, dAmt > 0 ? TransactionType.Debit : TransactionType.Credit, rawDesc));
            }

            foreach (var (txAmt, txType, txDesc) in toEmit)
            {
                string cleanDesc = txDesc;
                string? extId = null;
                
                if (bankCode == BankGalicia)     
                    (cleanDesc, extId) = ExtractGaliciaExternalId(txDesc);
                else if (bankCode == BankCredicoop)
                    (cleanDesc, extId) = ExtractCredicoopExternalId(txDesc);
                else if (bankCode == BankBbva)   
                    extId = ExtractBbvaExternalId(txDesc);

                currentTx = new BankTransaction
                {
                    Date        = date,
                    Description = cleanDesc,
                    Amount      = txAmt,
                    Type        = txType,
                    SourceBank  = bankCode,
                    ExternalId  = extId,
                };
                txs.Add(currentTx);
            }
        }
    }

    private static void ProcessContinuationRow(PdfRow row, string bankCode, double colDescStart, double colDescEnd, double rightDebit, double rightCredit, double rightSaldo, List<BankTransaction> txs, ref BankTransaction? currentTx)
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
            if (bankCode == BankGalicia && IsGaliciaSplitSummaryAmountRow(rawContDesc, contDebitAmts.Count, contCreditAmts.Count))
                return;
            var dAmt = contDebitAmts.Count > 0 ? contDebitAmts[0] : 0m;
            var cAmt = contCreditAmts.Count > 0 ? contCreditAmts[0] : 0m;
            var contType = dAmt > 0 ? TransactionType.Debit : TransactionType.Credit;
            var contAmt  = dAmt > 0 ? dAmt : cAmt;

            string? contExtId = null;
            if (bankCode == BankBbva)            
                contExtId = ExtractBbvaExternalId(rawContDesc);
            else if (bankCode == BankGalicia)    
                (rawContDesc, contExtId) = ExtractGaliciaExternalId(rawContDesc);
            else if (bankCode == BankCredicoop)
                (rawContDesc, contExtId) = ExtractCredicoopExternalId(rawContDesc);

            currentTx = new BankTransaction
            {
                Date        = currentTx!.Date, // Hereda la fecha de la última TX registrada
                Description = rawContDesc,
                Amount      = contAmt,
                Type        = contType,
                SourceBank  = bankCode,
                ExternalId  = contExtId,
            };
            txs.Add(currentTx);
        }
        // Si no hay importes, es texto que continúa la descripción anterior
        else if (descOnlyParts.Count > 0)
        {
            var extra = string.Join(" ", descOnlyParts).Trim();

            if (bankCode == BankGalicia && IsGaliciaRetentionSummaryText(extra))
                return;
            
            if (!string.IsNullOrWhiteSpace(extra) && !IsIrrelevantLine(extra))
            {
                var newDesc = Regex.Replace(currentTx!.Description + " " + extra, @"\s+", " ").Trim();
                string? mergedExtId = currentTx.ExternalId;
                string cleanedDesc = newDesc;

                if (bankCode == BankGalicia && mergedExtId is null)
                {
                    (cleanedDesc, mergedExtId) = ExtractGaliciaExternalId(newDesc);
                }
                else if (bankCode == BankCredicoop)
                {
                    (cleanedDesc, mergedExtId) = ExtractCredicoopExternalId(newDesc, mergedExtId);
                }

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

    #endregion

    #region Lógica Específica: BBVA

    // Valores nominales de comisión e IVA para transferencias BBVA (fijos por regulación)
    private const decimal BbvaComiAmount = 300m;
    private const decimal BbvaIvaAmount  = 63m;

    /// <summary>
    /// Limpia la descripción BBVA y vincula filas de comisión/IVA a su transferencia correspondiente
    /// para que cada transacción tenga firma única y no sea descartada como duplicado al importar.
    /// </summary>
    private static void LinkAndDisambiguateBbva(List<BankTransaction> txs)
    {
        // Pasada 1 — Limpiar descripciones y vincular COMI/IVA a la transferencia del grupo
        for (int i = 0; i < txs.Count; i++)
        {
            var tx   = txs[i];
            var desc = CleanBbvaDescription(tx.Description);

            // Si la descripción quedó vacía o ilegible, reconstruir por contexto
            if (string.IsNullOrWhiteSpace(desc) || desc.Length <= 1)
            {
                if (tx.Type == TransactionType.Debit && (tx.Amount == BbvaIvaAmount || tx.Amount == BbvaComiAmount))
                {
                    // Buscar el enlace [CUIT] en el grupo adyacente para filas de comisión
                    desc = InferBbvaFeeDescription(txs, i);
                }
                else
                {
                    desc = InferBbvaDescription(txs, i);
                }
            }

            // Vincular IDs a filas de comisiones para hacerlas únicas
            if (IsBbvaFeeRow(desc) && tx.Type == TransactionType.Debit && !desc.Contains('['))
            {
                string? link = null;
                
                // Buscar hacia adelante
                for (int j = i + 1; j < Math.Min(i + 6, txs.Count) && link == null; j++)
                    link = GetBbvaTransferLink(txs[j], tx.Date);
                    
                // Buscar hacia atrás
                for (int j = i - 1; j >= Math.Max(i - 6, 0) && link == null; j--)
                    link = GetBbvaTransferLink(txs[j], tx.Date);

                if (link != null) 
                    desc = $"{desc} [{link}]";
            }

            if (desc != tx.Description)
            {
                txs[i] = new BankTransaction
                {
                    Date        = tx.Date,
                    Description = desc,
                    Amount      = tx.Amount,
                    Type        = tx.Type,
                    SourceBank  = tx.SourceBank,
                    ExternalId  = tx.ExternalId,
                };
            }
        }

        // Pasada 2 — Contador secuencial para cualquier grupo duplicado restante
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        
        for (int i = 0; i < txs.Count; i++)
        {
            var tx  = txs[i];
            var key = $"{tx.Date}|{tx.Description}|{tx.Amount}|{tx.Type}";
            
            if (!seen.TryGetValue(key, out int n))
            {
                seen[key] = 1;
            }
            else
            {
                seen[key] = n + 1;
                txs[i] = new BankTransaction
                {
                    Date        = tx.Date,
                    Description = $"{tx.Description} [{n + 1}]",
                    Amount      = tx.Amount,
                    Type        = tx.Type,
                    SourceBank  = tx.SourceBank,
                    ExternalId  = tx.ExternalId,
                };
            }
        }
    }

    /// <summary>
    /// Busca la descripción más probable escaneando transacciones adyacentes.
    /// No retorna filas de comisión/IVA para evitar asignar descripciones erróneas.
    /// </summary>
    private static string InferBbvaDescription(List<BankTransaction> txs, int idx)
    {
        var tx = txs[idx];
        
        // Buscar hacia adelante
        for (int j = idx + 1; j < Math.Min(idx + 8, txs.Count); j++)
        {
            var c = txs[j];
            if (c.Date != tx.Date) break;
            
            var cd = CleanBbvaDescription(c.Description);
            if (c.Type == tx.Type && cd.Length > 2 && c.Amount != tx.Amount && !IsBbvaFeeRow(cd))
                return cd;
        }
        
        // Buscar hacia atrás
        for (int j = idx - 1; j >= Math.Max(idx - 8, 0); j--)
        {
            var c = txs[j];
            if (c.Date != tx.Date) break;
            
            var cd = CleanBbvaDescription(c.Description);
            if (c.Type == tx.Type && cd.Length > 2 && c.Amount != tx.Amount && !IsBbvaFeeRow(cd))
                return cd;
        }
        
        return tx.Description;
    }

    /// <summary>
    /// Reconstituye la descripción de filas de comisión o IVA buscando 
    /// el enlace [CUIT/importe] en el grupo adyacente.
    /// </summary>
    private static string InferBbvaFeeDescription(List<BankTransaction> txs, int idx)
    {
        var tx = txs[idx];
        var isComi = tx.Amount == BbvaComiAmount;

        int[] offsets = [1, -1, 2, -2, 3, -3, 4, -4, 5, -5, 6, -6];
        string? link = null;
        
        foreach (var off in offsets)
        {
            int j = idx + off;
            if (j < 0 || j >= txs.Count) continue;
            
            var c = txs[j];
            if (c.Date != tx.Date || c.Type != TransactionType.Debit) continue;
            
            var cd = CleanBbvaDescription(c.Description);
            
            // Buscar fila vecina que ya tenga el [link] incorporado
            var m = Regex.Match(cd, @"\[([^\]]+)\]$");
            if (m.Success && IsBbvaFeeRow(cd))
            {
                link = m.Groups[1].Value;
                break;
            }
            
            // Buscar transferencia directa para extraer CUIT/importe
            if (!IsBbvaFeeRow(cd) && c.Amount >= 10_000m)
            {
                link = c.ExternalId ?? $"{c.Amount:F2}";
                break;
            }
        }

        var baseDesc = isComi ? "COMI TRANSFERENCIA" : "IVA TASA GENERAL";
        return link != null ? $"{baseDesc} [{link}]" : baseDesc;
    }

    /// <summary>
    /// Determina si la descripción corresponde a una fila de comisión o IVA de BBVA.
    /// </summary>
    private static bool IsBbvaFeeRow(string description)
    {
        var upper = description.ToUpperInvariant();
        return upper.Contains("COMI TRANSFEREN")   ||
               upper.Contains("COMISION POR TRANS") ||
               upper.Contains("COMISIÓN POR TRANS") ||
               upper.Contains("IVA TASA GENERAL");
    }

    /// <summary>
    /// Devuelve el ID vinculante de una posible transferencia BBVA adyacente.
    /// </summary>
    private static string? GetBbvaTransferLink(BankTransaction candidate, DateOnly sameDate)
    {
        if (candidate.Date != sameDate) return null;
        if (IsBbvaFeeRow(candidate.Description)) return null; 
        if (candidate.Type != TransactionType.Debit || candidate.Amount < 10_000m) return null;
        
        return candidate.ExternalId ?? $"{candidate.Amount:F2}";
    }

    private static readonly Regex RxBbvaChannelPrefix = new(
        @"^[DC]\s+(?:\d{2,3}\s+)?", RegexOptions.Compiled);

    /// <summary>
    /// Elimina caracteres fantasma que BBVA introduce en la descripción por su diseño de columnas.
    /// </summary>
    private static string CleanBbvaDescription(string desc)
    {
        if (string.IsNullOrWhiteSpace(desc)) return desc;
        var s = desc.Trim();

        var si = s.IndexOf("SALDO AL", StringComparison.OrdinalIgnoreCase); 
        if (si >= 0) s = s[..si].TrimEnd();

        s = Regex.Replace(s, @"^.{1,3}\s+\d{1,2}/\d{2}\s+", "");
        s = RxBbvaChannelPrefix.Replace(s, "");
        s = Regex.Replace(s, @"\s+i\s+t\s*$", "");
        s = Regex.Replace(s, @"\s+-\s*$", "");
        s = Regex.Replace(s, @"\s+[a-zA-Z0-9.]\s*$", "");

        return s.Trim();
    }

    private static string[] SplitMergedBbvaDescription(string rawDesc)
    {
        var m = Regex.Match(rawDesc, @"(?<=\s)\b[DC]\b(?=\s)");
        if (m.Success)
        {
            return [rawDesc[..m.Index].TrimEnd(), rawDesc[m.Index..].TrimStart()];
        }
            
        return [rawDesc];
    }

    private static string? ExtractBbvaExternalId(string text)
    {
        var mBbva = Regex.Match(text, @"\bDNET\s+CREDITO\s+([A-Z]{1,3}\d{4,})\b", RegexOptions.IgnoreCase);
        if (!mBbva.Success) mBbva = Regex.Match(text, @"\bTR\.([A-Z]{1,3}\d{4,})\b", RegexOptions.IgnoreCase);
        if (!mBbva.Success) mBbva = Regex.Match(text, @"\b\d{3,6}-(\d{5,})\b");
        if (!mBbva.Success) mBbva = Regex.Match(text, @"\bCHEQUE\b.*\bN[°º]?\s*(\d{5,})\b", RegexOptions.IgnoreCase);
        if (!mBbva.Success) mBbva = Regex.Match(text, @"\bTRANSFERENCIA\s+(\d{8,11})\b", RegexOptions.IgnoreCase);
        
        return mBbva.Success ? mBbva.Groups[1].Value : null;
    }

    private static (Dictionary<(DateOnly, decimal), string> debitosAuto, Dictionary<string, BbvaChequeData> cheques, Dictionary<(DateOnly, decimal), string> transfersIn, Dictionary<(DateOnly, decimal), string> transfersOut) ParseBbvaSupplementaryData(List<PdfRow> rows, int? stmtYear)
    {
        var debitosAuto  = new Dictionary<(DateOnly, decimal), string>();
        var cheques      = new Dictionary<string, BbvaChequeData>(StringComparer.Ordinal);
        var transfersIn  = new Dictionary<(DateOnly, decimal), string>();
        var transfersOut = new Dictionary<(DateOnly, decimal), string>();

        int  section    = 0;
        bool inDataRows = false;

        foreach (var row in rows)
        {
            var line  = JoinCells(row);
            var upper = line.ToUpperInvariant();

            // Identificación de la sección actual
            if ((upper.Contains("DE EMISION") || upper.Contains("NRO DE CHEQUE")) && upper.Contains("FECHA")) 
            { 
                section = 4; inDataRows = true; continue; 
            }
            if (upper.Contains("EMPRESA") && upper.Contains("SERVICIO") && upper.Contains("REFERENCIA") && !upper.Contains("BANCO")) 
            { 
                section = 3; inDataRows = true; continue; 
            }
            if (upper.Contains("ENVIADAS") && (upper.Contains("ACEPTADAS") || upper.Contains("INFORMAC"))) 
            { 
                section = 2; inDataRows = false; continue; 
            }
            if (section == 2 && (upper.Contains("DOCUMENTO") || upper.Contains("APELLIDO")) && upper.Contains("IMPORTE")) 
            { 
                inDataRows = true; continue; 
            }
            if (upper.Contains("RECIBIDAS") && upper.Contains("INFORMAC")) 
            { 
                section = 1; inDataRows = false; continue; 
            }
            if (upper.Contains("BANCO") && upper.Contains("EMPRESA") && upper.Contains("FECHA")) 
            { 
                if (section == 1) inDataRows = true; 
                continue; 
            }

            if (!inDataRows || section == 0 || row.Cells.Count < 3 || !TryParseDate(row.Cells[0].Text, out var date, stmtYear)) 
                continue;

            switch (section)
            {
                case 1:
                    var (amtIn, companyIn) = ExtractBbvaTransferNameAndAmount(row, startIdx: 2);
                    if (amtIn > 0 && !string.IsNullOrWhiteSpace(companyIn)) 
                        transfersIn.TryAdd((date, amtIn), companyIn);
                    break;

                case 2:
                    var (amtOut, companyOut) = ExtractBbvaTransferNameAndAmount(row, startIdx: 1);
                    if (amtOut > 0 && !string.IsNullOrWhiteSpace(companyOut)) 
                        transfersOut.TryAdd((date, amtOut), companyOut);
                    break;

                case 3:
                    decimal amtDebito = 0;
                    var parts = new List<string>(4);
                    bool nameDone = false;
                    
                    for (int i = 1; i < row.Cells.Count; i++)
                    {
                        var txt = row.Cells[i].Text.Trim();
                        // Fin de fila útil: nro. de cuenta CC/CA
                        if (txt.Length <= 2 ? (txt == "CC" || txt == "CA") : ((txt.StartsWith("CC") || txt.StartsWith("CA")) && !char.IsLetter(txt[2]))) 
                            break; 
                            
                        if (txt == "$") continue; 
                        
                        if (IsArgentineAmount(txt)) 
                        { 
                            amtDebito = Math.Abs(ParseArgentineAmount(txt)); 
                            break; 
                        }
                        
                        if (!nameDone)
                        {
                            if (Regex.IsMatch(txt, @"^\d{5,}$") || txt == "VARIOS" || txt.StartsWith("TR.") || txt.Contains("/")) 
                                nameDone = true;
                            else 
                                parts.Add(txt);
                        }
                    }
                    if (amtDebito > 0 && parts.Count > 0) 
                        debitosAuto.TryAdd((date, amtDebito), string.Join(" ", parts).Trim());
                    break;

                case 4:
                    if (!TryParseDate(row.Cells[1].Text, out var pago, stmtYear)) break;
                    string nro = string.Empty;
                    
                    for (int i = 2; i < row.Cells.Count; i++)
                    {
                        var txt = row.Cells[i].Text.Trim();
                        if (IsArgentineAmount(txt)) break;
                        
                        if (Regex.IsMatch(txt, @"^\d{6,9}$") && string.IsNullOrEmpty(nro)) 
                        { 
                            nro = txt; 
                            break; 
                        }
                    }
                    if (!string.IsNullOrEmpty(nro)) 
                        cheques.TryAdd(nro, new BbvaChequeData(date, pago));
                    break;
            }
        }
        return (debitosAuto, cheques, transfersIn, transfersOut);
    }

    private static (decimal amount, string company) ExtractBbvaTransferNameAndAmount(PdfRow row, int startIdx)
    {
        decimal amount = 0;
        var parts = new List<string>(5);
        bool collectingName = true;

        for (int i = startIdx; i < row.Cells.Count; i++)
        {
            var txt = row.Cells[i].Text.Trim();
            
            // Fin de fila útil: nro. de cuenta CC/CA
            if (txt.Length <= 2 ? (txt == "CC" || txt == "CA") : ((txt.StartsWith("CC") || txt.StartsWith("CA")) && !char.IsLetter(txt[2]))) break; 
            
            if (txt == "$") continue;
            
            if (IsArgentineAmount(txt)) 
            { 
                amount = Math.Abs(ParseArgentineAmount(txt)); 
                break; 
            }

            if (collectingName)
            {
                // Limpiar prefijos CUIT
                var stripped = Regex.Replace(txt, @"^\d+[.\-]?", "");
                if (!string.IsNullOrWhiteSpace(stripped)) 
                    parts.Add(stripped);
                else if (!Regex.IsMatch(txt, @"^\d+$")) 
                    parts.Add(txt);

                if (txt == "VARIOS" || txt.StartsWith("TR.") || txt.StartsWith("E/CTA") ||
                    txt.Contains("/") || Regex.IsMatch(txt, @"^\d{5,}$") ||
                    (txt.Length == 1 && char.IsLetter(txt[0])) || parts.Count >= 5)
                {
                    collectingName = false;
                }
            }
        }
        return (amount, string.Join(" ", parts).Trim());
    }

    private static void ApplyBbvaEnrichments(
        List<BankTransaction> txs,
        Dictionary<(DateOnly, decimal), string> debitosAuto,
        Dictionary<string, BbvaChequeData> cheques,
        Dictionary<(DateOnly, decimal), string> transfersIn,
        Dictionary<(DateOnly, decimal), string> transfersOut)
    {
        var rxChequeNum = new Regex(@"\bN[°º]?\s*(\d{6,9})\b", RegexOptions.IgnoreCase);

        for (int i = 0; i < txs.Count; i++)
        {
            var tx = txs[i];
            var upper = tx.Description.ToUpperInvariant();
            string? newDesc = null;

            if (upper.Contains("DEBITO DIRECTO") && debitosAuto.TryGetValue((tx.Date, tx.Amount), out var empresa) && !string.IsNullOrWhiteSpace(empresa))
            {
                newDesc = $"DEBITO DIRECTO \u2192 {empresa.Trim()}"; 
            }

            if (upper.Contains("CHEQUE"))
            {
                var m = rxChequeNum.Match(tx.Description);
                if (m.Success && cheques.TryGetValue(m.Groups[1].Value, out var cheque))
                {
                    newDesc = $"{(newDesc ?? tx.Description).TrimEnd()} | EMIS.{cheque.Emision:dd/MM} VTO.{cheque.Pago:dd/MM}";
                }
            }

            // Enriquecimiento genérico buscando en las tablas de transferencias RECIBIDAS / ENVIADAS
            if (newDesc == null && !IsBbvaFeeRow(tx.Description) && !upper.Contains("CHEQUE"))
            {
                if (tx.Type == TransactionType.Credit && transfersIn.TryGetValue((tx.Date, tx.Amount), out var sender) && !string.IsNullOrWhiteSpace(sender))
                {
                    newDesc = $"{tx.Description.TrimEnd()} [{sender}]";
                }
                else if (tx.Type == TransactionType.Debit && transfersOut.TryGetValue((tx.Date, tx.Amount), out var recipient) && !string.IsNullOrWhiteSpace(recipient))
                {
                    newDesc = $"{tx.Description.TrimEnd()} \u2192 {recipient}"; // → Flecha
                }
            }

            if (newDesc != null)
            {
                txs[i] = new BankTransaction
                {
                    Date        = tx.Date, 
                    Description = newDesc, 
                    Amount      = tx.Amount,
                    Type        = tx.Type, 
                    SourceBank  = tx.SourceBank, 
                    ExternalId  = tx.ExternalId,
                };
            }
        }
    }

    #endregion

    #region Lógica Específica: Galicia

    private static (string cleanDesc, string? extId) ExtractGaliciaExternalId(string rawDesc)
    {
        var mNave = Regex.Match(rawDesc, @"^(NAVE\s*[-\u2013]?\s*[A-Z ]+?)\s+(\d{6,})\s*(.*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (mNave.Success)
        {
            var suffix = mNave.Groups[3].Value.Trim();
            var desc = suffix.Length > 0 ? $"{mNave.Groups[1].Value.Trim()} {suffix}" : mNave.Groups[1].Value.Trim();
            return (desc, mNave.Groups[2].Value);
        }
        
        var mPrisma = Regex.Match(rawDesc, @"^(ACREDITAMIENTO PRISMA[-\w ]*)\s+EST\.:\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (mPrisma.Success)
        {
            return (mPrisma.Groups[1].Value.Trim(), mPrisma.Groups[2].Value);
        }

        return (rawDesc, null);
    }

    #endregion

    #region Lógica Específica: MercadoPago

    private static List<BankTransaction> ParseMercadoPago(List<PdfRow> rows)
    {
        var rxOpId    = new Regex(@"^\d{8,}$", RegexOptions.Compiled);
        var rxPageNum = new Regex(@"^\d{1,3}/\d{1,3}$", RegexOptions.Compiled);
        var rxTxPrefix = new Regex(@"(?i)\b(liquidaci[oó]n|transferencia|pago\s+de|compra|devoluc?i[oó]n|impuesto|bonificaci[oó]n|carga\s+de)\b", RegexOptions.Compiled);

        static bool IsNonAlphanumericToken(string s) => s.Length > 0 && s.All(c => !char.IsLetterOrDigit(c));

        double xValor = -1;
        double xSaldo = -1;

        // Buscar posiciones de columnas clave
        foreach (var row in rows)
        {
            var upper = JoinCells(row).ToUpperInvariant();
            if (IsIrrelevantLine(upper)) continue;
            
            if (upper.Contains("VALOR") && upper.Contains("SALDO"))
            {
                xValor = FindColumnX(row, ["Valor", "VALOR"]);
                xSaldo = FindColumnX(row, ["Saldo", "SALDO"]);
                break;
            }
        }

        var anchors = new List<TxAnchor>();

        // Fase 1: Encontrar anclas de transacciones (fechas e importes)
        foreach (var row in rows)
        {
            if (row.Cells.Count == 0 || (row.Cells.Count == 1 && rxPageNum.IsMatch(row.Cells[0].Text.Trim()))) continue;
            if (!TryParseDate(row.Cells[0].Text, out var date)) continue;

            var amounts = new List<(double x, decimal value)>();
            foreach (var cell in row.Cells.Skip(1))
            {
                var txt = cell.Text.Trim();
                if (string.IsNullOrWhiteSpace(txt) || IsNonAlphanumericToken(txt)) continue;
                if (IsArgentineAmount(txt)) 
                    amounts.Add((cell.X, ParseArgentineAmount(txt)));
            }

            if (amounts.Count == 0) continue;

            decimal valor;
            if (amounts.Count == 1) 
            {
                valor = amounts[0].value;
            }
            else
            {
                var negatives = amounts.Where(a => a.value < 0).ToList();
                if (negatives.Count == 1) 
                    valor = negatives[0].value;
                else if (xValor > 0)      
                    valor = amounts.OrderBy(a => Math.Abs(a.x - xValor)).First().value;
                else                      
                    valor = amounts.OrderBy(a => a.x).First().value;
            }

            anchors.Add(new TxAnchor(row.PageNumber, row.Y, date, Math.Abs(valor), valor < 0 ? TransactionType.Debit : TransactionType.Credit));
        }

        if (anchors.Count == 0) return new List<BankTransaction>();

        const double maxDescDist = 40.0;
        var descLists = anchors.Select(_ => new List<string>()).ToList();
        var idLists   = anchors.Select(_ => string.Empty).ToList(); 

        // Fase 2: Recolectar la descripción basada en la proximidad (Y) al ancla
        foreach (var row in rows)
        {
            if (row.Cells.Count == 0 || (row.Cells.Count == 1 && rxPageNum.IsMatch(row.Cells[0].Text.Trim()))) continue;

            var lineUpper = JoinCells(row).ToUpperInvariant();
            if (IsIrrelevantLine(lineUpper) || lineUpper.Contains("MERCADO LIBRE S.R.L.") || lineUpper.Contains("MERCADOPAGO.COM") ||
                lineUpper.Contains("ENCUENTRA NUESTROS") || lineUpper.Contains("AV. CASEROS"))
            {
                continue;
            }

            int bestIdx = -1;
            double bestDist = double.MaxValue;
            for (int i = 0; i < anchors.Count; i++)
            {
                if (anchors[i].PageNumber != row.PageNumber) continue;
                
                double dist = Math.Abs(row.Y - anchors[i].RowY);
                if (dist < bestDist) 
                { 
                    bestDist = dist; 
                    bestIdx = i; 
                }
            }

            if (bestIdx < 0 || bestDist > maxDescDist) continue;

            foreach (var cell in row.Cells)
            {
                var txt = cell.Text.Trim();
                if (string.IsNullOrWhiteSpace(txt) || IsNonAlphanumericToken(txt) || IsArgentineAmount(txt) || TryParseDate(txt, out _)) 
                    continue;
                
                if (rxOpId.IsMatch(txt)) 
                {
                    idLists[bestIdx] = txt; 
                }
                else                     
                {
                    descLists[bestIdx].Add(txt); 
                }
            }
        }

        // Fase 3: Post-procesamiento. Limpieza y reasignación de textos "huérfanos" entre saltos de página
        for (int i = 0; i < anchors.Count - 1; i++)
        {
            var curRaw = Regex.Replace(string.Join(" ", descLists[i]), @"\s+", " ").Trim();
            var prefixMatches = rxTxPrefix.Matches(curRaw);
            
            if (prefixMatches.Count >= 2)
            {
                int secondIdx = prefixMatches[1].Index;
                string overflow = curRaw.Substring(secondIdx).Trim();
                descLists[i] = [curRaw.Substring(0, secondIdx).Trim()];

                if (anchors[i + 1].PageNumber > anchors[i].PageNumber)
                {
                    var nextRaw = Regex.Replace(string.Join(" ", descLists[i + 1]), @"\s+", " ").Trim();
                    if (!rxTxPrefix.IsMatch(nextRaw)) 
                    {
                        descLists[i + 1].Insert(0, overflow);
                    }
                }
            }

            if (anchors[i + 1].PageNumber > anchors[i].PageNumber)
            {
                var nextDesc = Regex.Replace(string.Join(" ", descLists[i + 1]), @"\s+", " ").Trim();
                
                if (!rxTxPrefix.IsMatch(nextDesc))
                {
                    int currentPage = anchors[i].PageNumber;
                    foreach (var row in rows.Where(r => r.PageNumber == currentPage && r.Cells.Count > 0))
                    {
                        if (anchors.Where(a => a.PageNumber == currentPage).All(a => Math.Abs(row.Y - a.RowY) > maxDescDist))
                        {
                            var orphanText = Regex.Replace(string.Join(" ", row.Cells.Select(c => c.Text.Trim()).Where(t => !string.IsNullOrWhiteSpace(t) && !IsNonAlphanumericToken(t))), @"\s+", " ").Trim();
                            if (rxTxPrefix.IsMatch(orphanText))
                            {
                                descLists[i + 1].Insert(0, orphanText);
                                break;
                            }
                        }
                    }
                }
            }
        }

        var txs = new List<BankTransaction>();
        for (int i = 0; i < anchors.Count; i++)
        {
            var desc = Regex.Replace(string.Join(" ", descLists[i]), @"\s+", " ").Trim();
            
            // Limpieza robusta de caracteres duplicados provocados por fallos del renderizado PDF de MercadoPago
            desc = Regex.Replace(desc, @"(?i)\bI+D+\s+d+e+\s+l+a+\s+o+p+e+r+a+c+i+[oó]+n+\b", "").Trim();
            desc = Regex.Replace(desc, @"(?i)\bF+E+C+H+A+\s+D+E+S+C+R+I+P+C+I+[oó]+n+\b", "").Trim();
            desc = Regex.Replace(desc, @"(?i)\bI+D+\s+d+e+\s+l+a+\b", "").Trim();
            desc = Regex.Replace(desc, @"(?i)\bo+p+e+r+a+c+i+[oó]+n+\b", "").Trim();
            desc = Regex.Replace(desc, @"(?i)\bF+E+C+H+A+\b", "").Trim();
            desc = Regex.Replace(desc, @"(?i)\bD+E+S+C+R+I+P+C+I+[oó]+n+\b", "").Trim();
            desc = Regex.Replace(desc, @"(?i)\bV+A+L+O+R+\b", "").Trim();
            desc = Regex.Replace(desc, @"(?i)\bS+A+L+D+O+\b", "").Trim();
            desc = Regex.Replace(desc, @"\s+", " ").Trim(); 
            
            if (string.IsNullOrWhiteSpace(desc) || desc.Length < 3) 
                desc = "Movimiento MercadoPago";

            txs.Add(new BankTransaction
            {
                Date        = anchors[i].Date,
                Description = desc,
                Amount      = anchors[i].Amount,
                Type        = anchors[i].Type,
                SourceBank  = BankMercadoPago,
                ExternalId  = string.IsNullOrWhiteSpace(idLists[i]) ? null : idLists[i],
            });
        }

        return txs.OrderBy(t => t.Date).ToList();
    }

    #endregion

    #region Helpers

    private static string JoinCells(PdfRow row) => string.Join(" ", row.Cells.Select(c => c.Text)).Trim();

    private static bool IsIrrelevantLine(string text)
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

    private static string CleanCredicoopDescription(string desc)
    {
        if (string.IsNullOrWhiteSpace(desc)) return desc;

        var clean = Regex.Replace(desc, @"(?is)Banco\s+Credicoop\s+Cooperativo\s+Limitado.*?www\.bancocredicoop\.coop", " ");
        clean = Regex.Replace(clean, @"(?is)Ctro\.\s+de\s+Contacto\s+Telefonico:.*?www\.bancocredicoop\.coop", " ");
        clean = Regex.Replace(clean, @"\s+", " ").Trim(' ', '-', '−');
        return clean;
    }

    private static (string cleanDesc, string? extId) ExtractCredicoopExternalId(string rawDesc, string? currentExtId = null)
    {
        var clean = CleanCredicoopDescription(rawDesc);
        if (!string.IsNullOrWhiteSpace(currentExtId))
            return (clean, currentExtId);

        var match = Regex.Match(clean, @"^0*(\d{5,})\s+(.+)$", RegexOptions.CultureInvariant);
        if (!match.Success)
            return (clean, null);

        var extId = match.Groups[1].Value;
        var desc = match.Groups[2].Value.Trim();
        return (desc, extId);
    }

    private static double FindColumnX(PdfRow row, string[] names)
    {
        foreach (var cell in row.Cells)
        {
            if (names.Any(n => cell.Text.Contains(n, StringComparison.OrdinalIgnoreCase)))
                return cell.X;
        }
        return -1;
    }

    private static bool TryParseDate(string text, out DateOnly date, int? referenceYear = null)
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
            
            // Heurística: Si no hay año de referencia y la fecha da más de 3 meses en el futuro, 
            // seguro es una transacción de fin del año pasado.
            if (referenceYear == null && date > DateOnly.FromDateTime(DateTime.Today.AddMonths(3)))
            {
                date = new DateOnly(year - 1, month, day);
            }
            
            return true;
        }
        catch { return false; }
    }

    private static int? DetectStatementYear(List<PdfRow> rows)
    {
        var rx = new Regex(@"^(\d{1,2})[/\-](\d{1,2})[/\-](\d{2,4})$");
        
        foreach (var row in rows)
        {
            foreach (var cell in row.Cells)
            {
                var m = rx.Match(cell.Text.Trim());
                if (!m.Success) continue;
                
                if (!int.TryParse(m.Groups[3].Value, out var y)) continue;
                if (y < 100) y += 2000;
                
                // Si el año es plausible, lo usamos
                if (y >= 2020 && y <= DateTime.Today.Year) return y;
            }
        }
        return null;
    }

    private static List<decimal> ExtractAmountsFromText(string text)
    {
        var rx = new Regex(@"-?[\d]{1,3}(?:\.[\d]{3})*,\d{2}|-?\d+,\d{2}");
        return rx.Matches(text)
            .Select(m => ParseArgentineAmount(m.Value))
            .Where(v => v != 0)
            .ToList();
    }

    private static bool IsArgentineAmount(string text) =>
        !string.IsNullOrWhiteSpace(text) && (RxAmount.IsMatch(text) || RxAmountShort.IsMatch(text));

    private static decimal ParseArgentineAmount(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0;
        
        var clean = raw.Trim().Replace(".", "").Replace(",", ".");
        return decimal.TryParse(clean, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var result) 
            ? result 
            : 0;
    }

    #endregion
}