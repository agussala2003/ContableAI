using ContableAI.Domain.Entities;
using ContableAI.Infrastructure.Services;
using FluentAssertions;

namespace ContableAI.Tests.Infrastructure;

/// <summary>
/// Regresión del bug de descripciones BBVA reportado el 15-7-2026: transferencias, cupones
/// y cheques aparecían asentados como "PAGOS AFIP" o "LEY NRO 25.413...".
///
/// Mecánica del bug: el agrupado por buckets fijos de Y partía la primera y la última fila
/// de cada página en dos (importes en un bucket, fecha+concepto en el adyacente). La fila
/// "solo importes" generaba una transacción sin descripción y con la fecha heredada del
/// movimiento anterior, la fila con el concepto real se descartaba, y luego
/// InferBbvaDescription "prestaba" la descripción de una transacción vecina del mismo día.
///
/// Fix: MergeSplitAmountRows re-une las filas partidas antes del parseo, y la inferencia por
/// vecino se eliminó (si una descripción sigue ilegible, queda el marcador explícito).
/// </summary>
public class BbvaSplitRowDescriptionTests
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
    [InlineData("0725.pdf")]
    [InlineData("0825.pdf")]
    [InlineData("0925.pdf")]
    [InlineData("1025.pdf")]
    [InlineData("1125.pdf")]
    [InlineData("1225 (1).pdf")]
    public void Parse_NoEmptyOrPlaceholderDescriptions(string fileName)
    {
        var txs = Parse(fileName);
        if (txs.Count == 0) return; // PDF no disponible en este entorno, omitir

        txs.Should().NotContain(t => string.IsNullOrWhiteSpace(t.Description),
            $"{fileName}: ninguna transacción puede quedar sin descripción");

        // En estos extractos todas las filas son legibles: si aparece el marcador es que el
        // merge de filas partidas dejó de funcionar.
        txs.Should().NotContain(t => t.Description.Contains("SIN DESCRIPCION LEGIBLE"),
            $"{fileName}: el merge de filas partidas debe recuperar todas las descripciones");
    }

    // Conteos de "PAGOS AFIP" verificados a mano contra la tabla principal de cada PDF.
    // El bug inflaba este número: movimientos ajenos heredaban la descripción del vecino.
    [Theory]
    [InlineData("0925.pdf", 4)]
    [InlineData("1125.pdf", 2)]
    [InlineData("1225 (1).pdf", 5)]
    public void Parse_PagosAfipCount_MatchesPdf(string fileName, int expectedCount)
    {
        var txs = Parse(fileName);
        if (txs.Count == 0) return;

        txs.Count(t => t.Description.Contains("PAGOS AFIP", StringComparison.OrdinalIgnoreCase))
            .Should().Be(expectedCount,
                $"{fileName}: nadie más que las filas reales del PDF puede decir PAGOS AFIP");
    }

    [Fact]
    public void Parse_ChequeAtPageBreak_KeepsOwnDateAndDescription()
    {
        // Caso replicado en la auditoría: "09/09 PAGO CHEQUE 48HS CAP.FED. N 90000390" al pie
        // de página quedaba como un débito del 08/09 con descripción "LEY NRO 25.413 SOBRE
        // CREDIT" (prestada del movimiento anterior).
        var txs = Parse("0925.pdf");
        if (txs.Count == 0) return;

        var cheque = txs.SingleOrDefault(t => t.Amount == 5_027_579.04m);
        cheque.Should().NotBeNull("el cheque de $5.027.579,04 debe existir como transacción");
        cheque!.Date.Should().Be(new DateOnly(2025, 9, 9), "la fecha es la de su propia fila, no la heredada");
        cheque.Description.Should().Contain("PAGO CHEQUE 48HS", "la descripción debe ser la de la línea real del extracto");
        cheque.Description.Should().Contain("90000390");
        cheque.Description.Should().NotContain("LEY NRO");
    }

    [Fact]
    public void Parse_LeyNro25413_OnlyOnRealTaxRows()
    {
        // Los impuestos Ley 25.413 son montos chicos; el bug estampaba esa descripción en
        // transferencias y cheques millonarios.
        var txs = Parse("0925.pdf");
        if (txs.Count == 0) return;

        var suspicious = txs.Where(t =>
            t.Description.Contains("LEY NRO 25.413", StringComparison.OrdinalIgnoreCase) &&
            t.Amount > 1_000_000m).ToList();

        suspicious.Should().BeEmpty(
            "ninguna transacción millonaria puede llevar la descripción del impuesto Ley 25.413");
    }
}
