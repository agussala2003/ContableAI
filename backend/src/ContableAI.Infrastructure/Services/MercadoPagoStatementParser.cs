using ContableAI.Domain.Entities;
using ContableAI.Domain.Enums;
using System.Text.RegularExpressions;
using static ContableAI.Infrastructure.Services.BankParsingHelpers;

namespace ContableAI.Infrastructure.Services;

/// <summary>
/// Parser de extractos MercadoPago. Formato propio (no tabular): reconstruye cada movimiento a
/// partir de anclas (fecha + importe firmado, cuyo signo define débito/crédito) y le asocia la
/// descripción y el ID de operación por proximidad vertical.
/// </summary>
internal sealed class MercadoPagoStatementParser : IBankStatementParser
{
    public string Bank => BankCodes.MercadoPago;
    private const string BankMercadoPago = BankCodes.MercadoPago;

    public IReadOnlyList<BankTransaction> Parse(IReadOnlyList<StatementLine> lines, string? fileName)
        => ParseMercadoPago([.. lines]);

    // ── Lógica específica MercadoPago (movida verbatim desde PdfBankParser) ─────
    private static List<BankTransaction> ParseMercadoPago(List<StatementLine> rows)
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
}
