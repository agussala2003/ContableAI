using ContableAI.Domain.Entities;
using ContableAI.Infrastructure.Services;
using FluentAssertions;

namespace ContableAI.Tests.Infrastructure;

/// <summary>
/// Regresión del bug de año reportado el 15-7-2026: los extractos BBVA de noviembre y
/// diciembre 2025 traen avisos del banco con fechas futuras ("a partir del 01/03/2026 se
/// aplicarán comisiones...", "información al: 02/01/2026"). Cuando el nombre del archivo no
/// matcheaba MMYY exacto (ej. "1225 (1).pdf", sufijo típico de descargas duplicadas), el
/// fallback de DetectStatementInfo tomaba la fecha MÁXIMA del documento y asignaba 2026 a
/// todos los movimientos. El fix: nombre de archivo tolerante a variantes + fallback por
/// moda (mes/año más frecuente) en lugar de máximo.
/// </summary>
public class StatementYearDetectionTests
{
    private static readonly string BaseDir =
        @"C:\Users\aguss\Documents\Projects\ContableAI\tests\extractos\BBVA FALLAS 15-7-2026";

    private static IReadOnlyList<BankTransaction> Parse(string fileName)
    {
        var path = Path.Combine(BaseDir, fileName);
        if (!File.Exists(path)) return [];
        var parser = new PdfBankParser();
        using var stream = File.OpenRead(path);
        return parser.Parse(stream, fileName).ToList();
    }

    [Theory]
    [InlineData("0725.pdf", 2025, 7)]
    [InlineData("0825.pdf", 2025, 8)]
    [InlineData("0925.pdf", 2025, 9)]
    [InlineData("1025.pdf", 2025, 10)]
    [InlineData("1125.pdf", 2025, 11)]
    [InlineData("1225 (1).pdf", 2025, 12)] // nombre con sufijo de descarga: activaba el fallback
    public void Parse_AssignsStatementYear_NotNoticeYear(string fileName, int expectedYear, int expectedMonth)
    {
        var txs = Parse(fileName);
        if (txs.Count == 0) return; // PDF no disponible en este entorno, omitir

        // Ningún movimiento puede caer en un año posterior al del extracto (el bug los mandaba a 2026)
        txs.Should().NotContain(t => t.Date.Year > expectedYear,
            $"{fileName} es de {expectedMonth}/{expectedYear}; las fechas de avisos del banco no deben definir el año");

        // La mayoría de los movimientos debe estar en el mes/año del extracto; el resto solo
        // puede ser el arrastre de los últimos días del mes anterior.
        var inPeriod = txs.Count(t => t.Date.Year == expectedYear && t.Date.Month == expectedMonth);
        inPeriod.Should().BeGreaterThan(txs.Count / 2,
            $"{fileName}: la mayoría de los movimientos debe caer en {expectedMonth}/{expectedYear}");

        var prevMonth = new DateOnly(expectedYear, expectedMonth, 1).AddMonths(-1);
        txs.Should().OnlyContain(
            t => (t.Date.Year == expectedYear && t.Date.Month == expectedMonth) ||
                 (t.Date.Year == prevMonth.Year && t.Date.Month == prevMonth.Month),
            $"{fileName}: solo se admite el mes del extracto y el arrastre del mes anterior");
    }

    [Theory]
    [InlineData("1125.pdf")]
    [InlineData("1225 (1).pdf")]
    public void Parse_NovemberAndDecember_NeverLeakIntoNextYear(string fileName)
    {
        var txs = Parse(fileName);
        if (txs.Count == 0) return;

        // Guarda explícita del síntoma reportado: "noviembre y diciembre se pasan como 2026"
        txs.Should().NotContain(t => t.Date.Year == 2026,
            $"{fileName}: ningún movimiento de nov/dic 2025 puede quedar en 2026");
    }
}
