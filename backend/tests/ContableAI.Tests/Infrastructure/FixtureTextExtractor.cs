using System.Text.RegularExpressions;
using ContableAI.Infrastructure.Services;

namespace ContableAI.Tests.Infrastructure;

/// <summary>
/// <see cref="IStatementTextExtractor"/> de prueba: convierte un fixture de texto monoespaciado
/// (columnas alineadas por espacios, como la salida cruda de Tesseract/PdfPig) en el modelo
/// posicional que consume <see cref="PdfBankParser"/>. Cada palabra se vuelve un token cuya X se
/// deriva de su posición de carácter en la línea, de modo que la alineación visual de columnas del
/// .txt reproduce la geometría de columnas de un extracto real — sin necesitar un PDF.
///
/// Permite reactivar la lógica de interpretación del parser (detección de columnas, matemática de
/// débito/crédito, exclusión de filas resumen) con aserciones reales sobre datos ficticios.
/// </summary>
internal sealed class FixtureTextExtractor : IStatementTextExtractor
{
    // Puntos por carácter. Convierte índices de carácter en coordenadas tipo-PDF para que la
    // tolerancia de 60 pts del parser (≈10 caracteres) se comporte igual que con un PDF real.
    private const double PointsPerChar = 6.0;

    // Separación vertical amplia entre líneas: evita que MergeSplitAmountRows (|ΔY| ≤ 4.5) una
    // filas del fixture por accidente. Y descendente imita la convención de PdfPig.
    private const double LineHeight = 100.0;

    private static readonly Regex RxToken = new(@"\S+", RegexOptions.Compiled);

    private readonly string _bank;
    private readonly StatementSource _source;
    private readonly IReadOnlyList<StatementLine> _lines;

    private FixtureTextExtractor(string bank, StatementSource source, IReadOnlyList<StatementLine> lines)
    {
        _bank   = bank;
        _source = source;
        _lines  = lines;
    }

    public StatementDocument Extract(Stream statementStream, string fileName)
        => new(_bank, _source, _lines);

    /// <summary>Construye un extractor a partir del contenido de texto de un fixture.</summary>
    public static FixtureTextExtractor FromText(
        string text, string bank, StatementSource source = StatementSource.Digital)
    {
        var rawLines = text.Replace("\r\n", "\n").Split('\n');
        var lines = new List<StatementLine>();

        for (int i = 0; i < rawLines.Length; i++)
        {
            var raw = rawLines[i];
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var cells = RxToken.Matches(raw)
                .Select(m => new StatementToken(
                    X:     m.Index * PointsPerChar,
                    Right: (m.Index + m.Length) * PointsPerChar,
                    Text:  m.Value))
                .ToList();

            // Y descendente: la primera línea del archivo queda "más arriba" en la página.
            lines.Add(new StatementLine(PageNumber: 1, Y: (rawLines.Length - i) * LineHeight, Cells: cells));
        }

        return new FixtureTextExtractor(bank, source, lines);
    }

    /// <summary>Construye un extractor leyendo un fixture .txt de TestData/text.</summary>
    public static FixtureTextExtractor FromFixture(
        string fixtureFileName, string bank, StatementSource source = StatementSource.Digital)
    {
        var path = TestData.PathTo("text", fixtureFileName);
        TestData.RequireFile(path);
        return FromText(File.ReadAllText(path), bank, source);
    }
}
