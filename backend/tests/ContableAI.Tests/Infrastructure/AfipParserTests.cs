using ContableAI.Infrastructure.Services;
using FluentAssertions;
using System.Text;

namespace ContableAI.Tests.Infrastructure;

/// <summary>
/// Tests del PdfAfipParserService.
/// Los tests con PDFs reales se saltan automáticamente si el archivo no está disponible
/// (usando el mismo patrón que BbvaNov2024ParseTest).
/// </summary>
public class AfipParserTests
{
    private readonly IAfipParserService _parser = new PdfAfipParserService();

    private const string VepFolder        = @"C:\Users\aguss\Documents\Projects\ContableAI\tests\afip\vep AFIP CONTABLE AI";
    private const string RootAfipFolder   = @"C:\Users\aguss\Documents\Projects\ContableAI\tests\afip";

    [Fact]
    public void ParsePdf_EmptyStream_ReturnsEmpty()
    {
        _parser.ParsePdf(new MemoryStream()).Should().BeEmpty();
    }

    [Fact]
    public void ParsePdf_InvalidBytes_ReturnsEmpty()
    {
        using var s = new MemoryStream(Encoding.UTF8.GetBytes("not a pdf"));
        _parser.ParsePdf(s).Should().BeEmpty();
    }

    // ── IVA A Pagar ───────────────────────────────────────────────────────────
    [Fact]
    public void Parse_IvaDJ_ExtractsTaxNameAndAmount()
    {
        var path = Path.Combine(VepFolder, "afip_vep_cuit_30703957540_nrovep_1383967092_nropago_284000592185.pdf");
        if (!File.Exists(path)) return;

        using var s = File.OpenRead(path);
        var result = _parser.ParsePdf(s).ToList();

        result.Should().HaveCount(1);
        result[0].TaxName.Should().Be("IVA A Pagar");
        result[0].Date.Should().Be(new DateOnly(2024, 12, 18));
        result[0].Amount.Should().Be(1907401.58m);
    }

    // ── Cargas Sociales (SIJP) ────────────────────────────────────────────────
    [Fact]
    public void Parse_SijpDJ_ExtractsCargas()
    {
        var path = Path.Combine(VepFolder, "afip_vep_cuit_30703957540_nrovep_1391317468_nropago_244000069711.pdf");
        if (!File.Exists(path)) return;

        using var s = File.OpenRead(path);
        var result = _parser.ParsePdf(s).ToList();

        result.Should().HaveCount(1);
        result[0].TaxName.Should().Be("Cargas Sociales");
        result[0].Date.Should().Be(new DateOnly(2025, 1, 9));
        result[0].Amount.Should().Be(3117696.21m);
    }

    // ── Pago IIBB (CM-PY) ─────────────────────────────────────────────────────
    [Fact]
    public void Parse_CmPyp_ExtractsPagoIIBB()
    {
        var path = Path.Combine(VepFolder, "afip_vep_cuit_30703957540_nrovep_1395814020_nropago_274000322223.pdf");
        if (!File.Exists(path)) return;

        using var s = File.OpenRead(path);
        var result = _parser.ParsePdf(s).ToList();

        result.Should().HaveCount(1);
        result[0].TaxName.Should().Be("Pago IIBB");
        result[0].Date.Should().Be(new DateOnly(2025, 1, 15));
        result[0].Amount.Should().Be(319631.88m);
    }

    // ── VEP Consolidado (VCON) ───────────────────────────────────────────────
    [Fact]
    public void Parse_VconPdf_ExtractsVepConsolidado()
    {
        var path = Path.Combine(RootAfipFolder, "afip_vep_cuit_30715723693_nrovep_1591844188_nropago_2163421783.pdf");
        if (!File.Exists(path)) return;

        using var s = File.OpenRead(path);
        var result = _parser.ParsePdf(s).ToList();

        result.Should().HaveCount(1);
        result[0].TaxName.Should().Be("VEP Consolidado");
        result[0].Date.Should().Be(new DateOnly(2026, 2, 26));
        result[0].Amount.Should().Be(274484.61m);
    }

    [Fact]
    public void Parse_Vcon2Pdf_ExtractsVepConsolidado()
    {
        var path = Path.Combine(VepFolder, "afip_vep_cuit_30703957540_nrovep_1509503815_nropago_234000204667 (1).pdf");
        if (!File.Exists(path)) return;

        using var s = File.OpenRead(path);
        var result = _parser.ParsePdf(s).ToList();

        result.Should().HaveCount(1);
        result[0].TaxName.Should().Be("VEP Consolidado");
        result[0].Date.Should().Be(new DateOnly(2025, 8, 27));
    }

    // ── Seg. Riesgo Trabajo (ARCA OTROS PAGOS + ASEG.RIESGO en body) ─────────
    [Fact]
    public void Parse_ArcaAseg_ExtractsSegRiesgoTrabajo()
    {
        var path = Path.Combine(VepFolder, "afip_vep_cuit_30703957540_nrovep_1489200424_nropago_224000561413 (1).pdf");
        if (!File.Exists(path)) return;

        using var s = File.OpenRead(path);
        var result = _parser.ParsePdf(s).ToList();

        result.Should().HaveCount(1);
        result[0].TaxName.Should().Be("Seg. Riesgo Trabajo");
        result[0].Amount.Should().Be(217274.00m);
    }

    // ── IVA A Pagar via ARCA OTROS PAGOS (body scan) ─────────────────────────
    [Fact]
    public void Parse_ArcaIva_ExtractsIvaAPagar()
    {
        var path = Path.Combine(RootAfipFolder, "afip_vep_cuit_30715723693_nrovep_1588342826_nropago_2162860848.pdf");
        if (!File.Exists(path)) return;

        using var s = File.OpenRead(path);
        var result = _parser.ParsePdf(s).ToList();

        result.Should().HaveCount(1);
        result[0].TaxName.Should().Be("IVA A Pagar");
        result[0].Date.Should().Be(new DateOnly(2026, 2, 25));
        result[0].Amount.Should().Be(10293065.85m);
    }

    // ── Honorarios Fiscales (HEF-RF) ─────────────────────────────────────────
    [Fact]
    public void Parse_HefRf_ExtractsHonorariosFiscales()
    {
        var path = Path.Combine(RootAfipFolder, "afip_vep_cuit_30715723693_nrovep_1452154187_nropago_2037436526 (1).pdf");
        if (!File.Exists(path)) return;

        using var s = File.OpenRead(path);
        var result = _parser.ParsePdf(s).ToList();

        result.Should().HaveCount(1);
        result[0].TaxName.Should().Be("Honorarios Fiscales");
        result[0].Amount.Should().Be(479285.82m);
    }

    // ── Plan de Facilidades (MIS FACILIDADES) ────────────────────────────────
    [Fact]
    public void Parse_MisFacilidades_ExtractsPlanFacilidades()
    {
        var path = Path.Combine(RootAfipFolder, "afip_vep_cuit_30715723693_nrovep_1566053347_nropago_2141556156.pdf");
        if (!File.Exists(path)) return;

        using var s = File.OpenRead(path);
        var result = _parser.ParsePdf(s).ToList();

        result.Should().HaveCount(1);
        result[0].TaxName.Should().Be("Plan de Facilidades");
        result[0].Date.Should().Be(new DateOnly(2026, 1, 2));
        result[0].Amount.Should().Be(3548562.58m);
    }

    // ── Cargas Sociales via ARCA OTROS PAGOS multa (body scan) ───────────────
    [Fact]
    public void Parse_AfipMulta_ExtractsCargas()
    {
        var path = Path.Combine(RootAfipFolder, "afip_vep_cuit_30715723693_nrovep_1250785244_nropago_1872100751 (1).pdf");
        if (!File.Exists(path)) return;

        using var s = File.OpenRead(path);
        var result = _parser.ParsePdf(s).ToList();

        result.Should().HaveCount(1);
        result[0].TaxName.Should().Be("Cargas Sociales");
        result[0].Amount.Should().Be(400.00m);
    }

    // ── VEP expirado sin IMPORTE PAGADO: debe ser ignorado ───────────────────
    [Fact]
    public void Parse_ExpiredVepWithoutImporte_ReturnsEmpty()
    {
        var path = Path.Combine(RootAfipFolder, "afip_vep_cuit_30715723693_nrovep_1181355860.pdf");
        if (!File.Exists(path)) return;

        using var s = File.OpenRead(path);
        var result = _parser.ParsePdf(s).ToList();

        result.Should().BeEmpty("el VEP expirado no tiene IMPORTE PAGADO ni Importe total a pagar");
    }

    // ── VEP pendiente (usa Fecha Generación + Importe total a pagar) ─────────
    [Fact]
    public void Parse_PendingVep_UsesGeneracionDateAndTotalImporte()
    {
        var path = Path.Combine(RootAfipFolder, "afip_vep_cuit_30715723693_nrovep_1593072109.pdf");
        if (!File.Exists(path)) return;

        using var s = File.OpenRead(path);
        var result = _parser.ParsePdf(s).ToList();

        result.Should().HaveCount(1);
        result[0].TaxName.Should().Be("Honorarios Fiscales");
        result[0].Date.Should().Be(new DateOnly(2026, 3, 2));
        result[0].Amount.Should().BeGreaterThan(0);
    }

    // ── PDF consolidado (ARCA - Seti - Consulta VEP): multi-fila ─────────────
    [Fact]
    public void Parse_ConsolidatedVep_ExtractsOnlyPaidMappedRows()
    {
        var path = Path.Combine(RootAfipFolder, "PDF consolidado VEP",
            "setidj_consultaveps_usuario_20224325399_fecha_20260531_204454.pdf");
        if (!File.Exists(path)) return;

        using var s = File.OpenRead(path);
        var result = _parser.ParsePdf(s).ToList();

        // 80 filas Pagado; se descartan 15 (14 ARCA + 1 AFIP sin detalle) → 65 mapeadas.
        result.Should().HaveCount(65);

        // Ninguna fila ARCA/AFIP genérica debe colarse.
        result.Should().NotContain(r => r.TaxName.StartsWith("ARCA") || r.TaxName.StartsWith("AFIP"));

        // Solo nombres canónicos conocidos.
        result.Select(r => r.TaxName).Distinct().Should().BeSubsetOf(new[]
        {
            "Cargas Sociales", "IVA A Pagar", "Pago IIBB", "Honorarios Fiscales", "VEP Consolidado",
        });

        // Desglose esperado por tipo.
        result.Count(r => r.TaxName == "Cargas Sociales").Should().Be(41);   // SIJPDJ
        result.Count(r => r.TaxName == "IVA A Pagar").Should().Be(11);       // IVA DJ
        result.Count(r => r.TaxName == "Pago IIBB").Should().Be(8);          // CM-SOP
        result.Count(r => r.TaxName == "Honorarios Fiscales").Should().Be(3);// HEF-RF
        result.Count(r => r.TaxName == "VEP Consolidado").Should().Be(2);    // VCON

        // Spot-check de una fila concreta (SIJPDJ04/26 → Cargas Sociales).
        result.Should().Contain(r =>
            r.TaxName == "Cargas Sociales" &&
            r.Amount == 9228670.33m &&
            r.Date == new DateOnly(2026, 5, 22));
    }

    // ── Smoke test: todos los PDFs del dataset deben devolver exactamente 1 resultado ──
    [Fact]
    public void Parse_AllVepDataset_EachYieldsOneResult()
    {
        if (!Directory.Exists(VepFolder)) return;

        var files = Directory.GetFiles(VepFolder, "*.pdf");
        files.Should().NotBeEmpty();

        var failures = new List<string>();
        foreach (var file in files)
        {
            using var s = File.OpenRead(file);
            var results = _parser.ParsePdf(s).ToList();
            if (results.Count != 1)
                failures.Add($"{Path.GetFileName(file)} → {results.Count} results");
        }

        failures.Should().BeEmpty(
            $"every paid VEP should yield exactly 1 result, but these failed:\n{string.Join("\n", failures)}");
    }
}
