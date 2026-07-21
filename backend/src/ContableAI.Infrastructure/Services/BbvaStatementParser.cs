using ContableAI.Domain.Entities;
using ContableAI.Domain.Enums;
using System.Text.RegularExpressions;
using static ContableAI.Infrastructure.Services.BankParsingHelpers;

namespace ContableAI.Infrastructure.Services;

/// <summary>
/// Parser de extractos BBVA. Sobre el motor tabular agrega: desdoblado de filas con dos
/// movimientos fusionados, extracción de id externo y el enriquecimiento con los anexos del
/// extracto (débitos automáticos, cheques y transferencias) que le da a cada movimiento una
/// descripción y firma únicas.
/// </summary>
internal sealed class BbvaStatementParser : TabularStatementParser
{
    public override string Bank => BankCodes.Bbva;
    private const string BankBbva = BankCodes.Bbva;

    private sealed record BbvaChequeData(DateOnly Emision, DateOnly Pago);

    /// <summary>BBVA fusiona a veces dos transacciones en una línea: se desdobla la descripción
    /// y se emparejan los segmentos con los importes (una "C " inicial marca un contraasiento).</summary>
    protected override List<Emission> BuildEmissions(List<decimal> debitAmts, List<decimal> creditAmts, string rawDesc)
    {
        int totalAmts = debitAmts.Count + creditAmts.Count;
        if (totalAmts < 2)
            return base.BuildEmissions(debitAmts, creditAmts, rawDesc);

        var descSegments = SplitMergedBbvaDescription(rawDesc);
        var toEmit = new List<Emission>(totalAmts);
        var pendingDebits  = new Queue<decimal>(debitAmts);
        var pendingCredits = new Queue<decimal>(creditAmts);

        foreach (var seg in descSegments)
        {
            bool isReversal = Regex.IsMatch(seg, @"^\s*C\s");

            if (isReversal && pendingDebits.Count > 0)
                toEmit.Add(new Emission(pendingDebits.Dequeue(), TransactionType.Debit, seg));
            else if (!isReversal && pendingCredits.Count > 0)
                toEmit.Add(new Emission(pendingCredits.Dequeue(), TransactionType.Credit, seg));
            else if (pendingDebits.Count > 0)
                toEmit.Add(new Emission(pendingDebits.Dequeue(), TransactionType.Debit, seg));
            else if (pendingCredits.Count > 0)
                toEmit.Add(new Emission(pendingCredits.Dequeue(), TransactionType.Credit, seg));
        }

        var lastSeg = descSegments.Length > 0 ? descSegments[^1] : rawDesc;
        while (pendingDebits.Count > 0)
            toEmit.Add(new Emission(pendingDebits.Dequeue(), TransactionType.Debit, lastSeg));
        while (pendingCredits.Count > 0)
            toEmit.Add(new Emission(pendingCredits.Dequeue(), TransactionType.Credit, lastSeg));

        return toEmit;
    }

    protected override (string desc, string? extId) ExtractExternalId(string desc) => (desc, ExtractBbvaExternalId(desc));

    protected override void PostProcess(List<BankTransaction> txs, List<StatementLine> rows, int? stmtYear, int? primaryMonth)
    {
        if (txs.Count == 0) return;

        // Limpieza de descripciones y desambiguación ANTES del enriquecimiento.
        LinkAndDisambiguateBbva(txs);

        var (debitosAuto, chequesMap, transfersIn, transfersOut) = ParseBbvaSupplementaryData(rows, stmtYear, primaryMonth);
        ApplyBbvaEnrichments(txs, debitosAuto, chequesMap, transfersIn, transfersOut);
    }

    // ── Lógica específica BBVA (movida verbatim desde PdfBankParser) ────────────
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

            // Si la descripción quedó vacía o ilegible, NUNCA copiar la de un vecino: eso
            // asentaba transferencias/cheques/cupones con conceptos ajenos ("PAGOS AFIP",
            // "LEY NRO 25.413..."). Solo se reconstruyen las filas de comisión/IVA, que son
            // identificables sin ambigüedad por su importe nominal fijo; el resto queda
            // marcado para revisión manual del contador.
            if (string.IsNullOrWhiteSpace(desc) || desc.Length <= 1)
            {
                if (tx.Type == TransactionType.Debit && (tx.Amount == BbvaIvaAmount || tx.Amount == BbvaComiAmount))
                {
                    // Buscar el enlace [CUIT] en el grupo adyacente para filas de comisión
                    desc = InferBbvaFeeDescription(txs, i);
                }
                else
                {
                    desc = UnreadableDescriptionMarker;
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
    /// Marcador para movimientos cuyo texto no pudo extraerse del PDF. Se prefiere exponer el
    /// problema al contador antes que inventar una descripción (el sistema anterior copiaba la
    /// de una transacción vecina del mismo día, generando conciliaciones erróneas).
    /// </summary>
    private const string UnreadableDescriptionMarker = "(MOVIMIENTO SIN DESCRIPCION LEGIBLE)";

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

    private static (Dictionary<(DateOnly, decimal), string> debitosAuto, Dictionary<string, BbvaChequeData> cheques, Dictionary<(DateOnly, decimal), string> transfersIn, Dictionary<(DateOnly, decimal), string> transfersOut) ParseBbvaSupplementaryData(List<StatementLine> rows, int? stmtYear, int? primaryMonth = null)
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

            if (!inDataRows || section == 0 || row.Cells.Count < 3 || !TryParseDate(row.Cells[0].Text, out var date, stmtYear, primaryMonth))
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
                    if (!TryParseDate(row.Cells[1].Text, out var pago, stmtYear, primaryMonth)) break;
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

    private static (decimal amount, string company) ExtractBbvaTransferNameAndAmount(StatementLine row, int startIdx)
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
}
