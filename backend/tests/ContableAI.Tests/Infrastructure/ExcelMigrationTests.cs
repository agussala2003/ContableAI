using ClosedXML.Excel;
using ContableAI.Domain.Entities;
using ContableAI.Domain.Enums;
using ContableAI.Infrastructure.Services;
using FluentAssertions;

namespace ContableAI.Tests.Infrastructure;

/// <summary>
/// COST-02: tras migrar EPPlus → ClosedXML, valida escritura y lectura de .xlsx con
/// round-trips sintéticos (no hay archivos Excel reales en el repo).
/// </summary>
public class ExcelMigrationTests
{
    private static MemoryStream BuildXlsx(Action<IXLWorksheet> fill)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Hoja1");
        fill(ws);
        var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;
        return ms;
    }

    // ── Escritura: ExportToExcel genera un .xlsx legible con los datos correctos ──
    [Fact]
    public void ExportToExcel_RoundTrip_WritesReadableWorkbook()
    {
        var tx = new BankTransaction
        {
            Date = new DateOnly(2025, 6, 10),
            Description = "Pago proveedor",
            Amount = 1500.50m,
            Type = TransactionType.Debit,
        };
        tx.Assign("Proveedores", null, false, "Manual");

        var bytes = new ExcelExportService().ExportToExcel([tx], "Mi Estudio SRL", 6, 2025);
        bytes.Should().NotBeEmpty();

        using var wb = new XLWorkbook(new MemoryStream(bytes));
        var ws = wb.Worksheets.First();

        ws.Cell("A1").GetString().Should().Be("Mi Estudio SRL");
        ws.Cell(4, 1).GetString().Should().Be("Fecha");       // encabezado
        ws.Cell(5, 2).GetString().Should().Be("Pago proveedor");
        ws.Cell(5, 3).GetDouble().Should().Be(1500.50);       // Debe
        ws.Cell(5, 5).GetString().Should().Be("Proveedores"); // cuenta asignada
    }

    // ── Lectura: parser Galicia sobre un .xlsx sintético ──────────────────────
    [Fact]
    public void GaliciaParser_Xlsx_ReadsTransactions()
    {
        var ms = BuildXlsx(ws =>
        {
            ws.Cell(1, 1).Value = "Fecha"; ws.Cell(1, 3).Value = "Descripción"; ws.Cell(1, 5).Value = "Importe";
            ws.Cell(2, 1).Value = "01/06/2025"; ws.Cell(2, 3).Value = "Pago proveedor"; ws.Cell(2, 5).Value = -1500.50;
            ws.Cell(3, 1).Value = "02/06/2025"; ws.Cell(3, 3).Value = "Cobro cliente";  ws.Cell(3, 5).Value = 2000;
        });

        var txs = new GaliciaParser().Parse(ms, "galicia.xlsx").ToList();

        txs.Should().HaveCount(2);
        txs[0].Date.Should().Be(new DateOnly(2025, 6, 1));
        txs[0].Description.Should().Be("Pago proveedor");
        txs[0].Amount.Should().Be(1500.50m);
        txs[0].Type.Should().Be(TransactionType.Debit);   // -1500.50 → egreso
        txs[1].Amount.Should().Be(2000m);
        txs[1].Type.Should().Be(TransactionType.Credit);  // +2000 → ingreso
    }

    // ── Lectura: parser BBVA "simple" (< 20 columnas) ─────────────────────────
    [Fact]
    public void BbvaParser_SimpleXlsx_ReadsTransaction()
    {
        var ms = BuildXlsx(ws =>
        {
            ws.Cell(1, 1).Value = "Fecha"; ws.Cell(1, 2).Value = "Concepto"; ws.Cell(1, 3).Value = "Importe";
            ws.Cell(2, 1).Value = "15/06/2025"; ws.Cell(2, 2).Value = "Compra insumos"; ws.Cell(2, 3).Value = -999.99;
        });

        var txs = new BbvaParser().Parse(ms, "bbva.xlsx").ToList();

        txs.Should().ContainSingle();
        txs[0].Date.Should().Be(new DateOnly(2025, 6, 15));
        txs[0].Description.Should().Be("Compra insumos");
        txs[0].Amount.Should().Be(999.99m);
        txs[0].Type.Should().Be(TransactionType.Debit);
    }
}
