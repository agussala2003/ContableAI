using Xunit;

namespace ContableAI.Tests;

/// <summary>
/// Resuelve, de forma portable y determinista, la carpeta de datos de prueba (PDFs de
/// extractos bancarios y VEPs de AFIP) que usan los tests de regresión de los parsers.
///
/// Prioridad de resolución:
///   1. Variable de entorno <c>CONTABLEAI_TESTDATA</c> (útil en CI o para apuntar a un
///      dataset local en otra ruta).
///   2. Carpeta <c>TestData</c> copiada al directorio de salida del proyecto de tests
///      (ver <c>ContableAI.Tests.csproj</c>, que copia backend/tests/TestData/**).
///
/// Los PDFs reales NO se versionan (contienen datos sensibles: CUITs, importes, cuentas).
/// Cada desarrollador/CI coloca sus PDFs anonimizados siguiendo
/// <c>backend/tests/TestData/README.md</c>. Cuando un archivo requerido no está presente,
/// el test se marca como <b>Skipped</b> (visible en el runner), nunca pasa en verde en
/// silencio: esa era justamente la falla que estos helpers vienen a corregir.
/// </summary>
public static class TestData
{
    /// <summary>Raíz del dataset de prueba (ver reglas de resolución en la clase).</summary>
    public static string Root { get; } =
        Environment.GetEnvironmentVariable("CONTABLEAI_TESTDATA") is { Length: > 0 } custom
            ? custom
            : System.IO.Path.Combine(AppContext.BaseDirectory, "TestData");

    /// <summary>Carpeta con los extractos bancarios (subcarpetas por banco).</summary>
    public static string Extractos => System.IO.Path.Combine(Root, "extractos");

    /// <summary>Carpeta con los comprobantes VEP de AFIP.</summary>
    public static string Afip => System.IO.Path.Combine(Root, "afip");

    /// <summary>Compone una ruta absoluta dentro del dataset a partir de segmentos relativos.</summary>
    public static string PathTo(params string[] relativeSegments) =>
        System.IO.Path.Combine([Root, .. relativeSegments]);

    /// <summary>
    /// Salta el test (lo reporta como Skipped) si el archivo de datos no está disponible.
    /// Usar dentro de un <c>[SkippableFact]</c>/<c>[SkippableTheory]</c>.
    /// </summary>
    public static void RequireFile(string absolutePath) =>
        Skip.IfNot(
            File.Exists(absolutePath),
            $"Falta el PDF de prueba '{absolutePath}'. Colocá los datos anonimizados según " +
            "backend/tests/TestData/README.md, o definí la variable de entorno CONTABLEAI_TESTDATA.");

    /// <summary>
    /// Salta el test si la carpeta no existe o no contiene ningún PDF. Devuelve los PDFs
    /// encontrados (ordenados) cuando sí hay datos.
    /// </summary>
    public static IReadOnlyList<string> RequirePdfs(string absoluteFolder)
    {
        var pdfs = Directory.Exists(absoluteFolder)
            ? Directory.EnumerateFiles(absoluteFolder, "*.pdf", SearchOption.TopDirectoryOnly).OrderBy(p => p).ToList()
            : [];

        Skip.If(
            pdfs.Count == 0,
            $"No hay PDFs de prueba en '{absoluteFolder}'. Colocá los datos anonimizados según " +
            "backend/tests/TestData/README.md, o definí la variable de entorno CONTABLEAI_TESTDATA.");

        return pdfs;
    }
}
