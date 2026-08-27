using ContableAI.Domain.Constants;
using ContableAI.Domain.Entities;
using ContableAI.Domain.Enums;
using System.Text.RegularExpressions;
using static ContableAI.Infrastructure.Services.BankParsingHelpers;

namespace ContableAI.Infrastructure.Services;

/// <summary>
/// Parser de extractos Banco Santander Argentina (PDF digital).
///
/// NO hereda de <see cref="TabularStatementParser"/> aunque el formato sea tabular, por dos
/// diferencias que el motor compartido no contempla y que no se pueden inyectar por sus hooks:
///
///   1. <b>Fila de saldo inicial.</b> Santander abre la tabla con una fila que TIENE fecha pero
///      solo trae saldo ("30/08/25 · Saldo Inicial · $ 2.548.842,58"). El motor compartido, al no
///      encontrar débito ni crédito posicionales, cae a <c>ExtractAmountsFromText</c> y emitiría
///      el saldo de apertura como si fuera un crédito — un movimiento inventado de varios
///      millones. No hay hook para desactivar ese fallback.
///   2. <b>Columna de descripción.</b> El motor la ubica buscando encabezados "DESC"/"CONCEPTO"/
///      "DETALLE"; Santander la titula "Movimiento", así que la detección falla y las filas de
///      continuación pasan a aceptar texto de CUALQUIER X — incluyendo pies de página legales y
///      números de página.
///
/// Ambas se arreglarían tocando <see cref="TabularStatementParser"/>, que hoy comparten BBVA,
/// Galicia, Credicoop y el genérico. Se descartó a propósito: los PDFs de regresión de esos
/// bancos no están versionados (ver <c>backend/tests/TestData/README.md</c>), así que un cambio
/// en el motor compartido no se puede validar acá y se estaría tocando a ciegas.
///
/// El símbolo de moneda viene SIEMPRE como celda propia a la izquierda del número
/// ("[442]$ [449-492]419.889,54"), nunca pegado ni con la palabra "pesos": esa aparece solo en
/// los bloques de resumen ("Total en pesos"), que no son parte de la tabla de movimientos.
/// </summary>
internal sealed class SantanderStatementParser : IBankStatementParser
{
    public string Bank => BankCodes.Santander;

    /// <summary>
    /// Tolerancia horizontal para decidir a qué columna pertenece un importe, comparando su borde
    /// derecho contra el del encabezado. En los extractos reales los importes alinean a la derecha
    /// casi exacto (desvíos de 2 a 4 puntos) y las columnas están separadas por ~86 puntos, así
    /// que 40 discrimina con margen de sobra en ambos sentidos.
    /// </summary>
    private const double AmountColumnTolerance = 40.0;

    /// <summary>Margen a la izquierda del encabezado "Movimiento" donde empieza la descripción.</summary>
    private const double DescriptionLeftMargin = 20.0;

    /// <summary>
    /// Celdas que son solo el signo de moneda y nunca forman parte de la descripción.
    ///
    /// El menos de un saldo en descubierto viaja PEGADO al símbolo y no al número: la fila se
    /// renderiza como "[-$] [22.633,76]", no como "[-22.633,76]". Por eso el grupo opcional: sin
    /// él, el saldo negativo de una cuenta corriente girada en descubierto se leería positivo y
    /// rompería la cadena de saldos contra el extracto del mes siguiente.
    /// </summary>
    private static readonly Regex RxCurrencySymbol = new(@"^(?<neg>-)?(?:\$|U\$S|US\$|USD)$", RegexOptions.Compiled);

    /// <summary>El número de comprobante es un entero suelto en su propia columna.</summary>
    private static readonly Regex RxComprobante = new(@"^\d{4,}$", RegexOptions.Compiled);

    /// <summary>CBU de 22 dígitos, para contar cuántas cuentas declara el documento.</summary>
    private static readonly Regex RxCbu = new(@"\d{22}", RegexOptions.Compiled);

    public IReadOnlyList<BankTransaction> Parse(IReadOnlyList<StatementLine> lines, string? fileName)
    {
        RejectConsolidatedStatement(lines);

        var txs = new List<BankTransaction>();
        var columns = Columns.Unmapped;
        var inTable = false;
        var currentPage = 0;
        BankTransaction? currentTx = null;

        var (stmtYear, primaryMonth) = DetectStatementInfo([.. lines], fileName);

        foreach (var row in lines)
        {
            // Cada página vuelve a declarar su encabezado, así que la tabla se cierra al cambiar
            // de página y se reabre recién al encontrarlo. Sin esto, la línea de identificación de
            // la cuenta que Santander imprime arriba de todo ("Cuenta Corriente Nº 575-000161/4
            // CBU: 0720...") cae dentro de la columna de descripción y se pega como continuación
            // al último movimiento de la página anterior.
            if (row.PageNumber != currentPage)
            {
                currentPage = row.PageNumber;
                inTable     = false;
                currentTx   = null;
            }

            var lineText = JoinCells(row);
            if (string.IsNullOrWhiteSpace(lineText)) continue;

            var upper = RemoveDiacritics(lineText).ToUpperInvariant();

            // El encabezado se repite en cada página: remapea las columnas y reabre la tabla.
            if (TryMapHeader(row, upper, out var mapped))
            {
                columns  = mapped;
                inTable  = true;
                currentTx = null;
                continue;
            }

            // Cierre de la tabla de movimientos. "Saldo total" es la fila de sumatoria y
            // "Detalle impositivo" abre el anexo de retenciones: nada después es un movimiento.
            // Sin este corte, el "Saldo" de la fila de sumatoria cae dentro de la columna de
            // descripción y se pegaría al final del último movimiento.
            if (IsEndOfMovements(upper))
            {
                inTable   = false;
                currentTx = null;
                continue;
            }

            if (!inTable || !columns.IsMapped) continue;

            if (StartsWithDate(row, columns, stmtYear, primaryMonth, out var date))
                ProcessMovementRow(row, columns, date, txs, ref currentTx);
            else if (currentTx != null)
                AppendContinuation(row, columns, txs, ref currentTx);
        }

        return [.. txs.OrderBy(t => t.Date)];
    }

    // ── Resúmenes consolidados ─────────────────────────────────────────────────

    /// <summary>
    /// Rechaza el resumen que trae VARIAS cuentas en un mismo PDF.
    ///
    /// Santander los arma apilando un bloque de movimientos por cuenta, cada uno con su propio
    /// encabezado de columnas y su fila "Total", y cierra con un "Saldo total" que es la suma de
    /// todas. Sin esta guarda el parser encadena los bloques y devuelve los movimientos de dos
    /// cuentas mezclados en una sola lista: al ordenarlos por fecha se intercalan, la columna de
    /// saldo deja de ser una serie coherente, y todo termina imputado a la cuenta equivocada.
    ///
    /// Existe una guarda equivalente y compartida (<see cref="PdfBankParser.DetectAccountIdentifiers"/>),
    /// pero solo mira las primeras 40 líneas del documento y en estos extractos el segundo bloque
    /// arranca más abajo, así que no llega a verlo. Ampliar esa ventana afectaría a todos los
    /// bancos —su umbral está calibrado contra un corpus de 47 extractos que no se puede correr
    /// acá, porque los PDFs no se versionan—, de modo que la verificación se hace local a
    /// Santander, sobre el documento completo.
    ///
    /// Se lanza <see cref="InvalidOperationException"/> porque es el canal que el pipeline de
    /// carga trata como mensaje apto para el usuario: el archivo se rechaza con su explicación y
    /// el resto de la carga continúa.
    /// </summary>
    private static void RejectConsolidatedStatement(IReadOnlyList<StatementLine> lines)
    {
        var cbus = new List<string>();

        foreach (var row in lines)
        {
            var line = JoinCells(row);

            // Solo los CBU ETIQUETADOS: una corrida de 22 dígitos suelta puede ser el número de
            // referencia de un movimiento, no una cuenta (mismo criterio que la guarda compartida).
            if (!line.Contains("CBU", StringComparison.OrdinalIgnoreCase)) continue;

            foreach (Match match in RxCbu.Matches(line))
                if (!cbus.Contains(match.Value))
                    cbus.Add(match.Value);
        }

        if (cbus.Count > 1)
            throw new InvalidOperationException(
                $"{PdfBankParser.MultipleAccountsDetectedError} (cuentas detectadas: {string.Join(", ", cbus)})");
    }

    // ── Encabezado y columnas ──────────────────────────────────────────────────

    /// <summary>
    /// Bordes derechos de las columnas numéricas y borde izquierdo de la descripción, leídos del
    /// encabezado "Fecha | Comprobante | Movimiento | Débito | Crédito | Saldo en cuenta".
    /// </summary>
    private readonly record struct Columns(double RightDebit, double RightCredit, double RightSaldo, double DescStart)
    {
        public static readonly Columns Unmapped = new(-1, -1, -1, -1);

        /// <summary>Sin débito y crédito no hay forma de clasificar un importe: la tabla no sirve.</summary>
        public bool IsMapped => RightDebit > 0 && RightCredit > 0;
    }

    private static bool TryMapHeader(StatementLine row, string upperLine, out Columns columns)
    {
        columns = Columns.Unmapped;

        if (!upperLine.Contains("FECHA") || !upperLine.Contains("DEBITO") || !upperLine.Contains("CREDITO"))
            return false;

        double rightDebit = -1, rightCredit = -1, rightSaldo = -1, descStart = -1;

        foreach (var cell in row.Cells)
        {
            var text = RemoveDiacritics(cell.Text).ToUpperInvariant();

            if (text == "DEBITO")      rightDebit  = cell.Right;
            else if (text == "CREDITO") rightCredit = cell.Right;
            else if (text == "SALDO")   rightSaldo  = cell.Right;
            else if (text == "MOVIMIENTO") descStart = cell.X;
        }

        columns = new Columns(rightDebit, rightCredit, rightSaldo, descStart);
        return columns.IsMapped;
    }

    private static bool IsEndOfMovements(string upperLine) =>
        upperLine.StartsWith("SALDO TOTAL") || upperLine.StartsWith("DETALLE IMPOSITIVO");

    private static bool StartsWithDate(
        StatementLine row, Columns columns, int? stmtYear, int? primaryMonth, out DateOnly date)
    {
        date = default;
        if (row.Cells.Count == 0) return false;

        // La fecha vive en la primera columna: un token con pinta de fecha en el medio de la fila
        // (ej. "Deb. automatico 01/09/2025 part ...") es parte de la descripción, no una fila nueva.
        var first = row.Cells[0];
        if (columns.DescStart > 0 && first.X >= columns.DescStart - DescriptionLeftMargin) return false;

        return TryParseDate(first.Text, out date, stmtYear, primaryMonth);
    }

    // ── Filas ──────────────────────────────────────────────────────────────────

    private void ProcessMovementRow(
        StatementLine row, Columns columns, DateOnly date,
        List<BankTransaction> txs, ref BankTransaction? currentTx)
    {
        decimal debit = 0m, credit = 0m, saldo = 0m;
        string? comprobante = null;
        var descParts = new List<string>();
        var pendingNegative = false;

        foreach (var cell in row.Cells.Skip(1))
        {
            if (IsArgentineAmount(cell.Text))
            {
                var amount = Math.Abs(ParseArgentineAmount(cell.Text));
                switch (ClassifyAmount(cell.Right, columns))
                {
                    // Débito y crédito son magnitudes: la dirección la lleva el Type, no el signo.
                    case AmountColumn.Debit:  debit  = amount; break;
                    case AmountColumn.Credit: credit = amount; break;
                    // El saldo sí conserva el signo: puede quedar en descubierto.
                    case AmountColumn.Saldo:  saldo  = pendingNegative ? -amount : amount; break;
                }
                pendingNegative = false;
                continue;
            }

            if (TryReadCurrencySymbol(cell.Text, out var isNegative))
            {
                pendingNegative = isNegative;
                continue;
            }

            // El signo solo aplica al importe que sigue inmediatamente al símbolo.
            pendingNegative = false;

            if (IsInDescriptionColumn(cell, columns))
                descParts.Add(cell.Text);
            else if (comprobante is null && RxComprobante.IsMatch(cell.Text))
                comprobante = cell.Text;
        }

        // Fila de saldo (apertura "Saldo Inicial", o cualquier fila que solo informe saldo): tiene
        // fecha pero no es un movimiento. Se corta acá a propósito, sin caer a ninguna extracción
        // de importes por texto, que es justamente lo que convertiría el saldo en un crédito falso.
        if (debit == 0m && credit == 0m)
        {
            currentTx = null;
            return;
        }

        var description = Regex.Replace(string.Join(" ", descParts).Trim(), @"\s+", " ");

        currentTx = new BankTransaction
        {
            Date         = date,
            Description  = description,
            Amount       = debit > 0m ? debit : credit,
            Type         = debit > 0m ? TransactionType.Debit : TransactionType.Credit,
            BalanceAfter = saldo,
            SourceBank   = Bank,
            ExternalId   = comprobante,
        };
        txs.Add(currentTx);
    }

    /// <summary>
    /// Fila sin fecha: es la segunda línea de la descripción del movimiento anterior ("De olivela
    /// beauty srl / - var / 30718952774"). Solo se toma el texto que cae dentro de la columna de
    /// descripción, que es lo que deja afuera los pies de página legales (X chico) y los números
    /// de página (X grande).
    /// </summary>
    private static void AppendContinuation(
        StatementLine row, Columns columns, List<BankTransaction> txs, ref BankTransaction? currentTx)
    {
        // La continuación ARRANCA en la columna de descripción, alineada con el renglón de arriba.
        // Una fila que empieza más a la izquierda es estructural —el pie institucional del banco,
        // la línea que identifica la cuenta, un título de sección— y no continúa nada. Sin esta
        // condición basta con que un párrafo largo cruce la columna para que sus palabras del medio
        // se peguen al último movimiento.
        if (row.Cells.Count == 0 || !IsInDescriptionColumn(row.Cells[0], columns)) return;

        var extraParts = row.Cells
            .Where(c => !IsArgentineAmount(c.Text)
                     && !TryReadCurrencySymbol(c.Text, out _)
                     && IsInDescriptionColumn(c, columns))
            .Select(c => c.Text)
            .ToList();

        if (extraParts.Count == 0) return;

        var extra = string.Join(" ", extraParts).Trim();
        if (string.IsNullOrWhiteSpace(extra) || IsIrrelevantLine(extra)) return;

        var merged = Regex.Replace($"{currentTx!.Description} {extra}", @"\s+", " ").Trim();

        currentTx = new BankTransaction
        {
            Date         = currentTx.Date,
            Description  = merged,
            Amount       = currentTx.Amount,
            Type         = currentTx.Type,
            BalanceAfter = currentTx.BalanceAfter,
            SourceBank   = currentTx.SourceBank,
            ExternalId   = currentTx.ExternalId,
        };
        txs[^1] = currentTx;
    }

    /// <summary>Lee una celda que es solo el símbolo de moneda, informando si trae el menos.</summary>
    private static bool TryReadCurrencySymbol(string text, out bool isNegative)
    {
        var match = RxCurrencySymbol.Match(text);
        isNegative = match.Success && match.Groups["neg"].Success;
        return match.Success;
    }

    // ── Clasificación posicional ───────────────────────────────────────────────

    private enum AmountColumn { None, Debit, Credit, Saldo }

    /// <summary>
    /// Asigna un importe a su columna por cercanía de su borde derecho al del encabezado. Los
    /// importes de Santander alinean a la derecha, así que el borde derecho es el discriminante
    /// estable (el izquierdo se corre según la cantidad de dígitos).
    /// </summary>
    private static AmountColumn ClassifyAmount(double right, Columns columns)
    {
        var best = AmountColumn.None;
        var bestDistance = AmountColumnTolerance;

        Consider(columns.RightDebit,  AmountColumn.Debit);
        Consider(columns.RightCredit, AmountColumn.Credit);
        Consider(columns.RightSaldo,  AmountColumn.Saldo);

        return best;

        void Consider(double headerRight, AmountColumn column)
        {
            if (headerRight <= 0) return;

            var distance = Math.Abs(right - headerRight);
            if (distance >= bestDistance) return;

            bestDistance = distance;
            best = column;
        }
    }

    private static bool IsInDescriptionColumn(StatementToken cell, Columns columns)
    {
        if (columns.DescStart <= 0) return false;

        // Acotada por los dos lados: a la izquierda deja afuera la fecha, el comprobante y los
        // textos institucionales del pie; a la derecha, todo lo que invada las columnas numéricas.
        return cell.X >= columns.DescStart - DescriptionLeftMargin
            && cell.Right <= columns.RightDebit;
    }
}
