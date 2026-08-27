using ContableAI.Domain.Constants;
using ContableAI.Domain.Entities;
using ContableAI.Domain.Enums;
using ContableAI.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContableAI.Tests.Infrastructure;

/// <summary>
/// Cobertura del parser de extractos Banco Santander, en dos niveles:
///
///   · <b>Fixture sintético</b> (siempre corre, incluso en CI): ejercita la interpretación sobre
///     datos ficticios versionados — clasificación por columna, símbolo de moneda en celda propia,
///     saldo negativo, continuación de descripción y exclusión de las filas de resumen.
///   · <b>Regresión sobre los 11 PDFs reales</b> (<c>Skippable</c>): valida la aritmética contra la
///     que declara el propio banco. Los PDFs no se versionan (ver <c>TestData/README.md</c>), así
///     que estos tests quedan en Skipped mientras no estén presentes — nunca en verde silencioso.
/// </summary>
public class SantanderParserTests
{
    private static readonly string SantanderFolder = TestData.PathTo("extractos", "SANTANDER");

    // ══════════════════════ Fixture sintético ══════════════════════

    private static List<BankTransaction> ParseFixture()
    {
        var extractor = FixtureTextExtractor.FromFixture("santander_mock_statement.txt", BankCodes.Santander);
        var parser    = new PdfBankParser(extractor, NullLogger<PdfBankParser>.Instance);
        return parser.Parse(Stream.Null, "santander_mock_statement.txt").ToList();
    }

    [Fact]
    public void Fixture_ClassifiesAmountsByColumn_AndKeepsDatesAndDescriptions()
    {
        var txs = ParseFixture();

        txs.Should().HaveCount(4, "el extracto tiene 4 movimientos; el saldo inicial y los totales no lo son");
        txs.Should().OnlyContain(t => t.SourceBank == BankCodes.Santander);
        txs.Should().OnlyContain(t => t.Currency == Currencies.Ars);
        txs.Should().BeInAscendingOrder(t => t.Date);

        txs[0].Date.Should().Be(new DateOnly(2025, 9, 1));
        txs[0].Type.Should().Be(TransactionType.Credit);
        txs[0].Amount.Should().Be(250_000.00m);
        txs[0].ExternalId.Should().Be("10000001", "el número de comprobante identifica al movimiento");

        txs[1].Type.Should().Be(TransactionType.Debit);
        txs[1].Amount.Should().Be(50_000.00m);

        txs[3].Type.Should().Be(TransactionType.Credit);
        txs[3].Amount.Should().Be(400_000.00m);
    }

    [Fact]
    public void Fixture_MergesContinuationLineIntoDescription()
    {
        var txs = ParseFixture();

        txs[0].Description.Should().Be("Transferencia recibida De proveedor demo srl / - var / 30111111111");
        txs[2].Description.Should().Be("Pago tarjeta de credito visa Deb. automatico 02/09/2025 part 01210000003");
    }

    [Fact]
    public void Fixture_NeverLeavesCurrencySymbolInsideDescription()
    {
        var txs = ParseFixture();

        txs.Should().OnlyContain(t => !t.Description.Contains('$'),
            "el símbolo de moneda viene como celda propia y no forma parte de la descripción");
    }

    [Fact]
    public void Fixture_KeepsSignOfOverdraftBalance()
    {
        var txs = ParseFixture();

        // El menos viaja pegado al símbolo ("-$ 300.000,00"), no al número.
        txs[2].BalanceAfter.Should().Be(-300_000.00m,
            "una cuenta corriente girada en descubierto informa el saldo en negativo");
    }

    [Fact]
    public void Fixture_ExcludesOpeningBalanceAndSummaryRows()
    {
        var txs = ParseFixture();

        // "Saldo Inicial" tiene fecha pero no es un movimiento: si se colara, aparecería como un
        // crédito de 1.000.000 — el error exacto que motivó no heredar del motor tabular.
        txs.Should().NotContain(t => t.Amount == 1_000_000.00m);
        txs.Should().NotContain(t => t.Description.Contains("Saldo", StringComparison.OrdinalIgnoreCase));

        // Las filas de "Total" / "Saldo total" y el anexo "Detalle impositivo" quedan afuera.
        txs.Should().NotContain(t => t.Amount == 1_500.00m);
        txs.Should().NotContain(t => t.Description.Contains("retencion", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Fixture_ExcludesPageFooterFromDescriptions()
    {
        var txs = ParseFixture();

        txs.Should().OnlyContain(t => !t.Description.Contains("sociedad anonima"),
            "el pie institucional se imprime fuera de la columna de descripción");
    }

    [Fact]
    public void Fixture_ChainsBalancesRowByRow()
    {
        var txs = ParseFixture();

        AssertBalanceChain(txs, "santander_mock_statement.txt");
    }

    // ══════════════════════ Regresión: 11 extractos reales ══════════════════════

    /// <summary>Un extracto real ya parseado, con lo necesario para encadenarlo con el siguiente.</summary>
    private sealed record ParsedMonth(
        string FileName,
        IReadOnlyList<BankTransaction> Transactions)
    {
        public DateOnly FirstDate      => Transactions[0].Date;
        public DateOnly LastDate       => Transactions[^1].Date;
        public decimal  ClosingBalance => Transactions[^1].BalanceAfter;

        /// <summary>
        /// Saldo con el que abre el extracto. Se deriva del primer movimiento en lugar de leer la
        /// fila "Saldo Inicial" —que el parser descarta por no ser un movimiento— justamente para
        /// que la aserción no dependa de la misma fila que se quiere verificar.
        /// </summary>
        public decimal OpeningBalance => Transactions[0].BalanceAfter - Signed(Transactions[0]);
    }

    private static decimal Signed(BankTransaction tx) =>
        tx.Type == TransactionType.Credit ? tx.Amount : -tx.Amount;

    /// <summary>
    /// Parsea la carpeta y separa los extractos de una sola cuenta de los resúmenes consolidados,
    /// que el parser rechaza con un mensaje apto para el usuario.
    /// </summary>
    private static (List<ParsedMonth> Single, List<string> Consolidated) ParseAllRealStatements()
    {
        var pdfs = TestData.RequirePdfs(SantanderFolder);
        var single = new List<ParsedMonth>();
        var consolidated = new List<string>();

        foreach (var pdfPath in pdfs)
        {
            var fileName = Path.GetFileName(pdfPath);
            using var stream = File.OpenRead(pdfPath);

            try
            {
                var txs = new PdfBankParser().Parse(stream, fileName).ToList();
                txs.Should().NotBeEmpty($"{fileName} debería producir movimientos");
                single.Add(new ParsedMonth(fileName, txs));
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("consolidado"))
            {
                consolidated.Add(fileName);
            }
        }

        return (single.OrderBy(m => m.FirstDate).ToList(), consolidated);
    }

    [SkippableFact]
    public void Real_AllStatements_AreDetectedAsSantander()
    {
        var pdfs = TestData.RequirePdfs(SantanderFolder);
        var extractor = new OcrStatementExtractor();

        foreach (var pdfPath in pdfs)
        {
            using var stream = File.OpenRead(pdfPath);
            var doc = extractor.Extract(stream, Path.GetFileName(pdfPath));

            doc.Bank.Should().Be(BankCodes.Santander, $"{Path.GetFileName(pdfPath)} es un extracto de Santander");
            doc.Source.Should().Be(StatementSource.Digital, "los extractos de Santander traen texto embebido, no requieren OCR");
        }
    }

    [SkippableFact]
    public void Real_ConsolidatedStatements_AreRejectedInsteadOfMerged()
    {
        var (single, consolidated) = ParseAllRealStatements();

        single.Should().NotBeEmpty("tiene que haber extractos de una sola cuenta para validar");

        // Un consolidado apila dos cuentas en un PDF. Mezclarlas rompería la serie de saldos y
        // imputaría movimientos ajenos a la cuenta elegida: se rechaza y el usuario sube cada
        // cuenta por separado.
        foreach (var month in single)
            month.Transactions.Should().OnlyContain(t => t.SourceBank == BankCodes.Santander);

        (single.Count + consolidated.Count).Should().Be(
            TestData.RequirePdfs(SantanderFolder).Count,
            "cada PDF tiene que quedar clasificado como de una cuenta o como consolidado");
    }

    [SkippableFact]
    public void Real_EachStatement_ChainsBalancesRowByRow()
    {
        var (months, _) = ParseAllRealStatements();

        foreach (var month in months)
            AssertBalanceChain(month.Transactions, month.FileName);
    }

    [SkippableFact]
    public void Real_ClosingBalanceOfEachMonth_MatchesOpeningBalanceOfTheNext()
    {
        var (months, _) = ParseAllRealStatements();

        months.Count.Should().BeGreaterThan(1, "hace falta más de un extracto para encadenar meses");

        var checkedPairs = 0;

        foreach (var m in months)
            Console.WriteLine(
                $"CADENA | {m.FileName,-42} | movs={m.Transactions.Count,4} | " +
                $"{m.FirstDate:dd/MM/yy}→{m.LastDate:dd/MM/yy} | " +
                $"apertura={m.OpeningBalance,16:N2} | cierre={m.ClosingBalance,16:N2}");

        for (int i = 1; i < months.Count; i++)
        {
            var previous = months[i - 1];
            var current  = months[i];

            // Solo se encadenan períodos contiguos. Un salto grande entre el final de uno y el
            // arranque del siguiente significa que falta un extracto en la carpeta, no que el
            // parser haya perdido filas.
            var gap = current.FirstDate.DayNumber - previous.LastDate.DayNumber;
            if (gap > 10) continue;

            current.OpeningBalance.Should().Be(
                previous.ClosingBalance,
                $"el saldo de cierre de {previous.FileName} ({previous.ClosingBalance:N2}) tiene que ser " +
                $"el de apertura de {current.FileName} ({current.OpeningBalance:N2}); una diferencia acá " +
                "significa que se perdieron o se inventaron movimientos");

            checkedPairs++;
        }

        // Sin esta aserción el test pasaría en verde aunque el bucle no comparara ni un solo par.
        checkedPairs.Should().BeGreaterThan(0, "al menos un par de meses consecutivos debe haberse verificado");
    }

    // ══════════════════════ Helper compartido ══════════════════════

    /// <summary>
    /// Verifica la serie de saldos fila por fila: cada saldo tiene que ser el anterior más el
    /// crédito o menos el débito. Es la aserción más fuerte que se puede hacer sobre un extracto,
    /// porque contrasta lo parseado contra la aritmética que imprime el propio banco: si se pierde
    /// una fila, se duplica o se le invierte el signo, la cadena se corta en esa posición exacta.
    /// </summary>
    private static void AssertBalanceChain(IReadOnlyList<BankTransaction> txs, string source)
    {
        for (int i = 1; i < txs.Count; i++)
        {
            var expected = txs[i - 1].BalanceAfter + Signed(txs[i]);

            txs[i].BalanceAfter.Should().Be(
                expected,
                $"{source}: el saldo del movimiento #{i} ({txs[i].Date:dd/MM/yy} · {txs[i].Description}) " +
                $"debería ser {expected:N2} = {txs[i - 1].BalanceAfter:N2} {(txs[i].Type == TransactionType.Credit ? "+" : "-")} {txs[i].Amount:N2}");
        }
    }
}
