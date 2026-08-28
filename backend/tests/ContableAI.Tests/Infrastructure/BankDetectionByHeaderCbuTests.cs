using ContableAI.Domain.Constants;
using ContableAI.Infrastructure.Services;
using FluentAssertions;

namespace ContableAI.Tests.Infrastructure;

/// <summary>
/// Regresión del bug reportado el 28/08/2026: un extracto de <b>Santander</b> se cargaba como si
/// fuera de <b>MercadoPago</b>.
///
/// La detección buscaba el nombre del banco en el texto de las primeras páginas — y ese texto
/// incluye las descripciones de los movimientos. Un extracto de Santander con una transferencia
/// "Transf recibida cvu dif titular De ... / mercado pago / ..." disparaba el chequeo de
/// MercadoPago, que corre ANTES que el de Santander (Santander va último a propósito: su logo es
/// vectorial y la palabra no aparece como texto extraíble).
///
/// El daño no era cosmético: con el banco equivocado se elige la estrategia de parseo equivocada.
/// El parser de MercadoPago deduce débito/crédito del SIGNO del importe, y Santander imprime los
/// importes sin signo en columnas Débito/Crédito separadas → los 14 débitos del extracto entraban
/// como créditos, más dos filas espurias (el "Saldo Inicial" y una del resumen de descubierto).
///
/// Ninguno de los PDFs versionados del corpus cubría el caso: todos los de Santander se detectan
/// por el nombre en el ARCHIVO, así que la ruta de contenido nunca se ejercitaba.
/// </summary>
public class BankDetectionByHeaderCbuTests
{
    private static StatementLine Row(int y, params string[] words) =>
        new(1, y, [.. words.Select((w, i) => new StatementToken(i * 50, i * 50 + 40, w))]);

    /// <summary>
    /// Encabezado de una cuenta corriente Santander (CBU 072…) seguido de un movimiento cuya
    /// descripción nombra a MercadoPago. Es la forma exacta del extracto que falló.
    /// </summary>
    private static List<StatementLine> SantanderRowsWithMercadoPagoInADescription() =>
    [
        Row(800, "Cuenta", "Corriente", "Nº", "575-000161/4", "CBU:", "0720575020000000016140"),
        Row(780, "Fecha", "Comprobante", "Movimiento", "Débito", "Crédito", "Saldo", "en", "cuenta"),
        Row(760, "11/07/23", "20565538", "Transf", "recibida", "cvu", "dif", "titular"),
        Row(750, "De", "luisa", "raquel", "serna", "ferrei/", "mercado", "pago", "/27943070062"),
    ];

    [Fact]
    public void HeaderCbu_WinsOverABankNameMentionedInsideAMovement()
    {
        var bank = OcrStatementExtractor.DetectBankFromRows(
            SantanderRowsWithMercadoPagoInADescription(), "5750001614_31-Jul-2023.pdf");

        bank.Should().Be(BankCodes.Santander,
            "el CBU del encabezado identifica a la cuenta que informa el documento; " +
            "'mercado pago' en la descripción de una transferencia solo nombra a la contraparte");
    }

    [Fact]
    public void HeaderCbu_IsReadOnlyWhenLabelled()
    {
        // Sin la etiqueta CBU/CVU, una corrida de 22 dígitos puede ser cualquier cosa (un número de
        // referencia, importes pegados). La detección tiene que ignorarla y caer al fallback.
        var rows = new List<StatementLine>
        {
            Row(800, "FEDERACION", "PATRO-0720575020000000016140"),
            Row(780, "Movimientos", "del", "período"),
        };

        OcrStatementExtractor.DetectBankFromHeaderCbu(rows).Should().BeNull();
    }

    [Fact]
    public void HeaderCbu_IsNotSearchedPastTheHeader()
    {
        // Un CBU ajeno impreso en el cuerpo (el de la contraparte de una transferencia) no puede
        // cambiar el banco del documento.
        var rows = Enumerable.Range(0, 60)
            .Select(i => Row(800 - i * 10, "11/07/23", "Movimiento", $"{i}"))
            .ToList();
        rows.Add(Row(100, "Transferencia", "a", "CBU", "0720575020000000016140"));

        OcrStatementExtractor.DetectBankFromHeaderCbu(rows).Should().BeNull();
    }

    [Fact]
    public void FileName_StillWinsOverEverythingElse()
    {
        // El usuario que renombra el archivo sigue mandando: es la señal más explícita que hay.
        var bank = OcrStatementExtractor.DetectBankFromRows(
            SantanderRowsWithMercadoPagoInADescription(), "EXTRACTO GALICIA 07-23.pdf");

        bank.Should().Be(BankCodes.Galicia);
    }

    [Fact]
    public void MercadoPago_IsStillDetectedByItsOwnHeader()
    {
        // El CBU no distingue billeteras virtuales (prefijo 000), así que MercadoPago se sigue
        // detectando por nombre. Esta es la contracara del fix: no puede haberse roto.
        var rows = new List<StatementLine>
        {
            Row(800, "Mercado", "Pago", "CVU:", "0000003100059687283158"),
            Row(780, "FECHA", "DESCRIPCIÓN", "ID", "de", "la", "operación", "VALOR", "SALDO"),
        };

        OcrStatementExtractor.DetectBankFromRows(rows, "Julio_2023.pdf")
            .Should().Be(BankCodes.MercadoPago);
    }
}
