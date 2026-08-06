using ClosedXML.Excel;
using ContableAI.Domain.Entities;
using ContableAI.Infrastructure.Services;
using FluentAssertions;

namespace ContableAI.Tests.Infrastructure;

/// <summary>
/// v1.1 / Epic B — La hoja "Formulario de Asiento" consolida SIEMPRE por (cuenta, lado): una
/// cuenta que recibe importes en el Debe y en el Haber genera dos filas independientes y nunca
/// se netea. Es el control de integridad de las cuentas puente ("VALORES EN TRANSITO"): si el
/// Debe no iguala al Haber, falta procesar el otro extremo de una transferencia.
///
/// Estas aserciones deben mantenerse en sintonía con <c>accountGroups</c> de
/// <c>journal-page.ts</c>, que implementa la misma consolidación del lado de la UI.
/// </summary>
public class JournalExportSplitTests
{
    private const string Bridge  = "VALORES EN TRANSITO";
    private const string Galicia = "Banco Galicia";
    private const string Mp      = "Banco Mercado Pago";

    private static JournalEntry Entry(string description, string debitAccount, string creditAccount, decimal amount) =>
        new()
        {
            Date        = new DateOnly(2026, 3, 10),
            Description = description,
            CompanyId   = Guid.NewGuid(),
            Lines =
            [
                new JournalEntryLine { Account = debitAccount,  Amount = amount, IsDebit = true  },
                new JournalEntryLine { Account = creditAccount, Amount = amount, IsDebit = false },
            ],
        };

    /// <summary>
    /// Escenario canónico: transferencia entre cuentas propias (Galicia → Mercado Pago) más un
    /// gasto. Los dos extremos de la transferencia pasan por la cuenta puente, uno por cada lado.
    /// </summary>
    private static IXLWorksheet BuildFormulario()
    {
        List<JournalEntry> entries =
        [
            Entry("TRANSF MISMA TITULARIDAD", Bridge, Galicia, 100_000m), // salida del Galicia
            Entry("TRANSF MISMA TITULARIDAD", Mp, Bridge,      100_000m), // entrada en MP
            Entry("PAGO PROVEEDOR",           "PROVEEDORES", Galicia, 5_000m),
        ];

        var bytes = new ExcelExportService()
            .ExportJournalEntriesToExcel(entries, "Empresa Demo SRL", 3, 2026);

        var wb = new XLWorkbook(new MemoryStream(bytes));
        return wb.Worksheet("Formulario de Asiento");
    }

    [Fact]
    public void Formulario_AccountOnBothSides_EmitsTwoRowsAndDoesNotNet()
    {
        var ws = BuildFormulario();

        // Las filas de datos arrancan en la 7 (encabezados en la 6).
        // Orden: primero el Debe (alfabético), después el Haber (alfabético).
        ws.Cell(9, 1).GetString().Should().Be($"{Bridge} (Debe)");
        ws.Cell(9, 2).GetDouble().Should().Be(100_000);
        ws.Cell(9, 3).IsEmpty().Should().BeTrue("la fila del Debe no lleva importe en la columna Haber");

        ws.Cell(11, 1).GetString().Should().Be($"{Bridge} (Haber)");
        ws.Cell(11, 3).GetDouble().Should().Be(100_000);
        ws.Cell(11, 2).IsEmpty().Should().BeTrue("la fila del Haber no lleva importe en la columna Debe");
    }

    [Fact]
    public void Formulario_OneSidedAccount_HasNoSuffix()
    {
        var ws = BuildFormulario();

        // Solo Debe → nombre limpio, sin "(Debe)".
        ws.Cell(7, 1).GetString().Should().Be(Mp);
        ws.Cell(8, 1).GetString().Should().Be("PROVEEDORES");

        // Solo Haber → nombre limpio, con los dos movimientos del Galicia consolidados.
        ws.Cell(10, 1).GetString().Should().Be(Galicia);
        ws.Cell(10, 3).GetDouble().Should().Be(105_000);
    }

    [Fact]
    public void Formulario_NeverEmitsANettedRowForATwoSidedAccount()
    {
        var ws = BuildFormulario();

        var labels = ws.Column(1)
            .CellsUsed()
            .Select(c => c.GetString())
            .ToList();

        labels.Should().NotContain(Bridge,
            "una cuenta con saldo en ambos lados nunca debe aparecer como una única fila neteada");
        labels.Should().Contain($"{Bridge} (Debe)").And.Contain($"{Bridge} (Haber)");
    }

    [Fact]
    public void Formulario_SplitDoesNotChangeTotals()
    {
        var ws = BuildFormulario();

        // Fila TOTAL, inmediatamente después de las 5 filas de datos (7..11).
        ws.Cell(12, 1).GetString().Should().Be("TOTAL");

        // El split reagrupa, no altera importes: 100.000 + 5.000 + 100.000 por lado.
        var debe  = ws.Column(2).Cells(7, 11).Sum(c => c.IsEmpty() ? 0 : c.GetDouble());
        var haber = ws.Column(3).Cells(7, 11).Sum(c => c.IsEmpty() ? 0 : c.GetDouble());

        debe.Should().Be(205_000);
        haber.Should().Be(205_000);
    }
}
