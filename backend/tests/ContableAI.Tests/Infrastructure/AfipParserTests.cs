using ContableAI.Infrastructure.Services;
using FluentAssertions;
using System.Text;

namespace ContableAI.Tests.Infrastructure;

/// <summary>
/// Tests del PdfAfipParserService.
/// Verifican que el parser extrae correctamente los datos de los PDFs de VEP (AFIP/ARCA).
/// </summary>
public class AfipParserTests
{
    private readonly IAfipParserService _parser = new PdfAfipParserService();

    private static Stream ToPdfStream(string content) =>
        new MemoryStream(Encoding.UTF8.GetBytes(content));

    // Los tests reales de ParsePdf requieren PDFs válidos generados por AFIP.
    // Se agregan aquí como placeholder para ser completados con archivos de prueba reales.
    [Fact]
    public void ParsePdf_NullOrEmptyStream_ReturnsEmpty()
    {
        var results = _parser.ParsePdf(new MemoryStream()).ToList();
        results.Should().BeEmpty();
    }
}
