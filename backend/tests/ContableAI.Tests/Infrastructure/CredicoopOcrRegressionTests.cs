using ContableAI.Domain.Constants;
using ContableAI.Domain.Enums;
using ContableAI.Infrastructure.Services;
using FluentAssertions;

namespace ContableAI.Tests.Infrastructure;

/// <summary>
/// Extracto de Credicoop escaneado (0 palabras digitales → ruta OCR). En producción el OCR ni
/// arrancaba (faltaban los symlinks de las libs nativas en el Dockerfile) y, una vez andando,
/// Tesseract partía "399.644,61" en "399.644," + "61" y la fila se perdía.
/// </summary>
public class CredicoopOcrRegressionTests
{
    [Fact]
    public void MergeSplitAmountTokens_JoinsAmountSplitAtDecimalComma()
    {
        var tokens = new List<StatementToken>
        {
            new(199, 280, "438156"),
            new(413, 537, "Servicios"),
            new(970, 1107, "399.644,"),
            new(1084, 1111, "61"),
        };

        var merged = OcrStatementExtractor.MergeSplitAmountTokens(tokens);

        merged.Select(t => t.Text).Should().Equal("438156", "Servicios", "399.644,61");
        merged[^1].X.Should().Be(970);
        merged[^1].Right.Should().Be(1111);
    }

    [Fact]
    public void MergeSplitAmountTokens_LeavesDistantTokensAlone()
    {
        var tokens = new List<StatementToken>
        {
            new(970, 1107, "1.234,"),
            new(1300, 1320, "50"),   // otra columna: no es la cola del importe
        };

        OcrStatementExtractor.MergeSplitAmountTokens(tokens)
            .Select(t => t.Text).Should().Equal("1.234,", "50");
    }

    [SkippableFact]
    public void Parse_CredicoopScanned_MatchesPrintedBalances()
    {
        // El motor de producción es el Tesseract de apt en Linux; el binario de Windows del
        // paquete NuGet es otra versión y lee distinto, así que acá no se compara.
        Skip.IfNot(OperatingSystem.IsLinux(), "Regresión de OCR calibrada contra el Tesseract de Linux.");

        var path = Path.Combine(TestData.Extractos, "CREDICOOP", "JULIO 2025 CREDICOOP CULTIVARAS.pdf");
        TestData.RequireFile(path);

        using var stream = File.OpenRead(path);
        var statement = new PdfBankParser().ParseStatement(stream, "extracto.pdf");
        var txs = statement.Transactions;

        statement.Bank.Should().Be(BankCodes.Credicoop);
        txs.Should().HaveCount(25);

        // Saldo al 31/07/25 (314.130,11) − saldo anterior (2.473.747,92)
        txs.Sum(t => t.Type == TransactionType.Credit ? t.Amount : -t.Amount).Should().Be(-2_159_617.81m);
    }
}
