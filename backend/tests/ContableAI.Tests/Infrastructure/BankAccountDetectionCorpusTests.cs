using ContableAI.Infrastructure.Services;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace ContableAI.Tests.Infrastructure;

/// <summary>
/// F1.d — Gate de calidad del enrutamiento automático: mide, contra TODO el corpus real de
/// extractos, con qué frecuencia se logra identificar la cuenta bancaria del documento.
///
/// No es un test de "pasa/no pasa" sobre un archivo puntual: es la medición que decide si un banco
/// puede habilitarse en modo automático o tiene que arrancar en modo manual. Por eso imprime la
/// tabla por banco además de afirmar el umbral.
///
/// El corpus no se versiona (datos sensibles): sin él los tests quedan Skipped, nunca en verde
/// silencioso.
/// </summary>
public class BankAccountDetectionCorpusTests(ITestOutputHelper output)
{
    private sealed record Detection(string Bank, string Folder, string File, string? Account, string? Cbu)
    {
        public bool Identified => Account is not null || Cbu is not null;

        /// <summary>Clave con la que el enrutamiento buscaría la cuenta.</summary>
        public string Key => $"{Account ?? "-"}/{Cbu ?? "-"}";
    }

    /// <summary>Archivos que el parser rechazó (no aportan a la medición). Se reportan aparte.</summary>
    private static readonly List<string> rejected = [];

    private static List<Detection> RunCorpus()
    {
        rejected.Clear();

        Skip.IfNot(Directory.Exists(TestData.Extractos),
            $"No hay corpus de extractos en '{TestData.Extractos}'.");

        var pdfs = Directory
            .EnumerateFiles(TestData.Extractos, "*.pdf", SearchOption.AllDirectories)
            .OrderBy(p => p)
            .ToList();

        Skip.If(pdfs.Count == 0, $"No hay PDFs en '{TestData.Extractos}'.");

        var parser = new PdfBankParser();
        var results = new List<Detection>();

        foreach (var pdf in pdfs)
        {
            try
            {
                using var stream = File.OpenRead(pdf);
                var statement = parser.ParseStatement(stream, Path.GetFileName(pdf));
                results.Add(new Detection(
                    statement.Bank,
                    Path.GetFileName(Path.GetDirectoryName(pdf)!),
                    Path.GetFileName(pdf),
                    statement.DetectedAccountNumber,
                    statement.DetectedCbu));
            }
            catch (InvalidOperationException)
            {
                // Extracto rechazado por el parser: mezcla de monedas, o PDF escaneado cuando
                // Tesseract no tiene sus datos de idioma instalados. No cuenta para la medición:
                // sin texto no hay nada que detectar, y el usuario recibe un error accionable.
                rejected.Add(Path.GetFileName(pdf));
            }
        }

        return results;
    }

    [SkippableFact]
    public void DetectionRate_PerBank_Report()
    {
        var results = RunCorpus();

        output.WriteLine($"Corpus: {results.Count} extractos legibles, {rejected.Count} rechazados");
        foreach (var r in rejected)
            output.WriteLine($"  [rechazado] {r}");
        output.WriteLine("");
        output.WriteLine($"{"BANCO",-14} {"TOTAL",5} {"IDENT.",6} {"NRO",4} {"CBU",4}  TASA");
        output.WriteLine(new string('-', 52));

        foreach (var g in results.GroupBy(r => r.Bank).OrderBy(g => g.Key))
        {
            var total = g.Count();
            var ident = g.Count(r => r.Identified);
            var acc   = g.Count(r => r.Account is not null);
            var cbu   = g.Count(r => r.Cbu is not null);
            output.WriteLine($"{g.Key,-14} {total,5} {ident,6} {acc,4} {cbu,4}  {100.0 * ident / total,5:F0}%");
        }

        var overall = 100.0 * results.Count(r => r.Identified) / results.Count;
        output.WriteLine(new string('-', 52));
        output.WriteLine($"{"TOTAL",-14} {results.Count,5} {results.Count(r => r.Identified),6}             {overall,5:F0}%");
        output.WriteLine("");

        foreach (var r in results.Where(r => !r.Identified))
            output.WriteLine($"[sin identificar] {r.Bank,-12} {r.File}");

        output.WriteLine("");
        output.WriteLine("Identificadores por carpeta (cada carpeta = una cuenta real):");
        foreach (var g in results.GroupBy(r => r.Folder).OrderBy(g => g.Key))
        {
            var distinct = g.Select(r => r.Key).Distinct().ToList();
            output.WriteLine($"  {g.Key,-28} {g.Count(),2} archivos → {distinct.Count} identificador(es)");
            foreach (var k in distinct)
                output.WriteLine($"      {k}");
        }

        results.Should().NotBeEmpty();
    }

    /// <summary>
    /// La propiedad que de verdad importa para enrutar: dos extractos de la misma cuenta nunca
    /// deben dar identificadores CONTRADICTORIOS. Si un mes leyera un número y otro mes uno
    /// distinto para la misma cuenta, cada archivo crearía su propia cuenta provisional y el
    /// multi-cuenta se volvería inusable.
    ///
    /// Una tasa de detección del 100% no dice nada sobre esto: leer un número distinto en cada
    /// archivo también da 100%.
    ///
    /// La contradicción se mide con la misma semántica que usa el router: dos extractos que
    /// comparten CBU tienen que compartir número de cuenta (y viceversa). Que uno de los dos no
    /// haya podido leer el número corto NO es contradicción — el router los une igual por el CBU,
    /// que es justamente el caso de Mercado Pago en el corpus.
    /// </summary>
    [SkippableFact]
    public void DetectedIdentifiers_AreNeverContradictoryForTheSameAccount()
    {
        var results = RunCorpus();

        foreach (var byCbu in results.Where(r => r.Cbu is not null).GroupBy(r => r.Cbu))
        {
            var accounts = byCbu.Select(r => r.Account).Where(a => a is not null).Distinct().ToList();
            accounts.Count.Should().BeLessThanOrEqualTo(1,
                $"los extractos con CBU {byCbu.Key} son de la misma cuenta, pero se leyeron " +
                $"números distintos: {string.Join(" | ", accounts)}");
        }

        foreach (var byAccount in results.Where(r => r.Account is not null).GroupBy(r => r.Account))
        {
            var cbus = byAccount.Select(r => r.Cbu).Where(c => c is not null).Distinct().ToList();
            cbus.Count.Should().BeLessThanOrEqualTo(1,
                $"los extractos de la cuenta {byAccount.Key} son la misma cuenta, pero se leyeron " +
                $"CBUs distintos: {string.Join(" | ", cbus)}");
        }
    }

    /// <summary>
    /// Lo que NO puede pasar: devolver un identificador con forma inválida. Un número mal leído se
    /// enrutaría a la cuenta equivocada —o crearía una cuenta basura— y eso es peor que no detectar
    /// nada, que simplemente le pide al usuario elegir la cuenta a mano.
    /// </summary>
    [SkippableFact]
    public void DetectedIdentifiers_AreWellFormed()
    {
        var results = RunCorpus();

        foreach (var r in results)
        {
            if (r.Cbu is not null)
            {
                r.Cbu.Should().MatchRegex(@"^\d{22}$",
                    $"{r.File}: un CBU/CVU siempre tiene exactamente 22 dígitos");
            }

            if (r.Account is not null)
            {
                r.Account.Should().MatchRegex(@"^\d+$",
                    $"{r.File}: el número de cuenta se normaliza a solo dígitos");
                r.Account.Length.Should().BeInRange(6, 22,
                    $"{r.File}: un número fuera de ese rango no es una cuenta bancaria");
            }
        }
    }

    /// <summary>
    /// Umbral de habilitación del modo automático sobre el corpus completo. Es deliberadamente
    /// conservador: los bancos que no lo alcanzan se operan en modo manual (el usuario elige la
    /// cuenta en la Dropzone), no se les baja la exigencia.
    /// </summary>
    [SkippableFact]
    public void DetectionRate_MeetsMinimumThreshold()
    {
        var results = RunCorpus();
        var rate = 100.0 * results.Count(r => r.Identified) / results.Count;

        rate.Should().BeGreaterThanOrEqualTo(80,
            "por debajo de este umbral el enrutamiento automático genera más fricción que valor");
    }
}
