using ContableAI.Domain.Constants;
using ContableAI.Domain.Enums;
using ContableAI.Infrastructure.Services;
using FluentAssertions;

namespace ContableAI.Tests.Infrastructure;

/// <summary>
/// Los dos formatos de Banco Ciudad que el parser legacy no leía (devolvían 0 movimientos):
/// el export "Últimos movimientos" del home banking y el resumen de cuenta oficial. El neto
/// esperado sale de los saldos impresos (saldo final − saldo previo al primer movimiento), así
/// que valida a la vez cantidad, importes y signo de cada movimiento.
/// </summary>
public class CiudadParseRegressionTests
{
    private static readonly string CiudadFolder = TestData.PathTo("extractos", "BANCO CIUDAD");

    private static ParsedStatement Parse(string fileName, string? uploadName = null)
    {
        var path = Path.Combine(CiudadFolder, fileName);
        TestData.RequireFile(path);
        using var stream = File.OpenRead(path);
        return new PdfBankParser().ParseStatement(stream, uploadName ?? fileName);
    }

    [SkippableTheory]
    // Home banking "Últimos movimientos"
    [InlineData("mayo ciudad 2026.pdf",              12,   -109_036.00)]
    [InlineData("junio ciudad 2026.pdf",             12,   -209_636.00)]
    [InlineData("julio ciudad 2026.pdf",              4,   -206_000.00)]
    [InlineData("agosto ciudad 2026.pdf",            16,  1_069_900.00)] // 2 páginas
    [InlineData("Ciudad Agosto-Septiembre 2025.pdf", 12,   -628_437.17)] // orden descendente, IVA incluido
    // Resumen de cuenta oficial
    [InlineData("Ciudad 122025.pdf",                  2,    347_900.00)]
    [InlineData("Ciudad 122025 1.pdf",                3, -2_012_000.00)]
    public void Parse_Ciudad_MatchesPrintedBalances(string fileName, int expectedCount, double expectedNet)
    {
        var statement = Parse(fileName);
        var txs = statement.Transactions;

        statement.Bank.Should().Be(BankCodes.Ciudad);
        statement.Currency.Should().Be(Currencies.Ars);
        txs.Should().HaveCount(expectedCount);
        txs.Should().OnlyContain(t => t.Amount > 0);

        var net = txs.Sum(t => t.Type == TransactionType.Credit ? t.Amount : -t.Amount);
        net.Should().Be((decimal)expectedNet);
    }

    [SkippableTheory]
    [InlineData("mayo ciudad 2026.pdf")]
    [InlineData("Ciudad 122025.pdf")]
    public void Parse_Ciudad_BothFormatsResolveToTheSameAccount(string fileName)
    {
        // Home banking: "CC $ 000000290000016566"; resumen: "CUENTA NÚMERO 0001656/6".
        // Antes el primero detectaba el CUIT y el segundo "016566": dos cuentas provisionales.
        Parse(fileName).DetectedAccountNumber.Should().Be("00016566");
    }

    [SkippableFact]
    public void Parse_CiudadHomeBanking_IsDetectedWithoutBankInFileName()
    {
        var statement = Parse("julio ciudad 2026.pdf", uploadName: "movimientos.pdf");

        statement.Bank.Should().Be(BankCodes.Ciudad);
        statement.Transactions.Should().HaveCount(4);
    }

    [SkippableFact]
    public void Parse_CiudadHomeBanking_KeepsComprobanteAndCleanDescription()
    {
        var tx = Parse("mayo ciudad 2026.pdf").Transactions
            .Single(t => t.ExternalId == "27255132");

        tx.Date.Should().Be(new DateOnly(2026, 4, 1));
        tx.Type.Should().Be(TransactionType.Debit);
        tx.Amount.Should().Be(500_000m);
        tx.Description.Should().Be("CHEQUE P/CAMARA");
    }
}
