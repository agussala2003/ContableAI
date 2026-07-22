using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PDFtoImage;
using SkiaSharp;
using System.Text;
using Tesseract;
using UglyToad.PdfPig;
using PdfPage = UglyToad.PdfPig.Content.Page;

namespace ContableAI.Infrastructure.Services;

/// <summary>
/// Implementación real de <see cref="IStatementTextExtractor"/>: encapsula TODA la lectura física
/// del PDF. Elige entre la ruta digital (texto embebido vía PdfPig, análisis posicional por
/// coordenadas) y la ruta OCR (render con PDFium + Tesseract) según cuánta información digital
/// tenga el archivo, y detecta el banco. Es la única clase que conoce PdfPig/Tesseract, dejando a
/// <see cref="PdfBankParser"/> libre para depender solo de la abstracción (DIP).
/// </summary>
internal sealed class OcrStatementExtractor : IStatementTextExtractor
{
    private readonly ILogger<OcrStatementExtractor> _logger;

    public OcrStatementExtractor(ILogger<OcrStatementExtractor> logger) => _logger = logger;
    public OcrStatementExtractor() => _logger = NullLogger<OcrStatementExtractor>.Instance;

    private const long MaxBytes = 150 * 1024 * 1024; // 150 MB hard limit
    private const int  OcrDpi   = 200;

    // Límite de OCR concurrente: Tesseract + render de PDF son CPU-bound y pesados en memoria: sin
    // este freno, N jobs de Hangfire ejecutando OCR en simultáneo pueden saturar CPU y memoria del
    // proceso igual que antes lo hacían N requests HTTP concurrentes. Bloquea el hilo del worker de
    // Hangfire que lo espera (no hay problema: son hilos de background, no de atención de requests).
    private static readonly SemaphoreSlim OcrGate = new(2, 2);

    public StatementDocument Extract(Stream statementStream, string fileName)
    {
        if (!statementStream.CanSeek)
            throw new InvalidOperationException("El stream del PDF debe ser buscable (seekable).");

        if (statementStream.Length > MaxBytes)
            throw new InvalidOperationException(
                $"El archivo PDF supera el límite de 150 MB ({statementStream.Length / 1_048_576} MB).");

        statementStream.Position = 0;

        // Se trabaja directo sobre el Stream recibido (PdfPig y PDFtoImage soportan Stream de forma
        // nativa) — antes acá se copiaba a un MemoryStream y se materializaba un byte[] adicional
        // (dos copias redundantes del PDF completo en memoria managed, sumadas a las que ya existían
        // río arriba en el endpoint/handler). Eliminarlas reduce el pico de memoria por archivo.
        using var doc = PdfDocument.Open(statementStream);
        var pages = doc.GetPages().ToList();

        var totalWords = pages.Take(3).Sum(p => p.GetWords().Count());
        _logger.LogDebug("[PDF] '{File}' — {Pages} pags, {Words} palabras digitales en primeras 3 pags",
            fileName, pages.Count, totalWords);

        if (totalWords >= 30)
        {
            // PDF digital: extracción de texto directa (líneas crudas; el merge de filas partidas
            // lo aplica el parser, que es donde vive la lógica de interpretación).
            var bank = DetectBank(pages, fileName);
            _logger.LogDebug("[PDF] Ruta DIGITAL — banco detectado: {Bank}", bank);
            var rows = ExtractPositionalRows(pages);
            _logger.LogDebug("[PDF] Filas posicionales extraídas: {RowCount}", rows.Count);
            return new StatementDocument(bank, StatementSource.Digital, rows);
        }

        // PDF escaneado: pipeline OCR
        _logger.LogDebug("[PDF] Ruta OCR — {Words} palabras insuficientes para ruta digital", totalWords);
        List<StatementLine> ocrRows;
        OcrGate.Wait();
        try
        {
            var tessPath = GetTessDataPath();
            _logger.LogDebug("[PDF] tessdata path: {Path} — existe: {Exists}", tessPath, Directory.Exists(tessPath));
            statementStream.Position = 0;
            ocrRows = ExtractRowsViaOcr(statementStream);
            _logger.LogDebug("[PDF] OCR completado — {RowCount} filas extraídas", ocrRows.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PDF] OCR falló para '{File}': {Msg}", fileName, ex.Message);
            throw new InvalidOperationException(
                $"OCR no disponible o falló al procesar el PDF escaneado (palabras digitales detectadas: {totalWords}). " +
                $"Detalle: {ex.Message}", ex);
        }
        finally
        {
            OcrGate.Release();
        }

        if (ocrRows.Count == 0)
            throw new InvalidOperationException(
                "No fue posible extraer texto del PDF con OCR. " +
                "Verifique que el archivo sea un extracto bancario válido.");

        var ocrBank = DetectBankFromRows(ocrRows, fileName);
        return new StatementDocument(ocrBank, StatementSource.Ocr, ocrRows);
    }

    /// <summary>
    /// Determina a qué banco pertenece el PDF analizando el nombre del archivo
    /// y una muestra del texto de las primeras páginas.
    /// </summary>
    private static string DetectBank(IList<PdfPage> pages, string fileName)
    {
        var normalizedFileName = (fileName ?? string.Empty).ToUpperInvariant()
            .Replace("_20", " ")
            .Replace("%20", " ");

        if (normalizedFileName.Contains("BBVA"))        return BankCodes.Bbva;
        if (normalizedFileName.Contains("GALICIA"))     return BankCodes.Galicia;
        if (normalizedFileName.Contains("CREDICOOP"))   return BankCodes.Credicoop;
        if (normalizedFileName.Contains("CIUDAD"))      return BankCodes.Ciudad;

        if (normalizedFileName.Contains("MERCADOPAGO") ||
            normalizedFileName.Contains("_MP_") ||
            normalizedFileName.Contains(" MP ") ||
            normalizedFileName.Contains("-MP-"))        return BankCodes.MercadoPago;

        // Fallback: Analizar el contenido de las primeras dos páginas
        var sample = new StringBuilder();
        foreach (var page in pages.Take(2))
        {
            foreach (var word in page.GetWords())
            {
                sample.Append(word.Text).Append(' ');
            }
        }

        var text = sample.ToString().ToUpperInvariant();

        if (text.Contains("BBVA") || text.Contains("CREANDO OPORTUNIDADES") || text.Contains("CBU 0170"))
            return BankCodes.Bbva;

        if (text.Contains("GALICIA"))
            return BankCodes.Galicia;

        if (text.Contains("CREDICOOP") || text.Contains("COOPERATIVA DE CRED"))
            return BankCodes.Credicoop;

        if (text.Contains("MERCADO PAGO") || text.Contains("MERCADOPAGO") || text.Contains("00000031") || text.Contains("MERCADO LIBRE"))
            return BankCodes.MercadoPago;

        // Ciudad: account format 3-029- (bank code 029) or explicit REFERENCIA column header
        if (text.Contains("3-029-") || text.Contains("BANCO CIUDAD"))
            return BankCodes.Ciudad;

        return BankCodes.Generic;
    }

    /// <summary>
    /// Agrupa las palabras del PDF en filas (StatementLine) basándose en su coordenada Y.
    /// Utiliza una tolerancia de 3 puntos para unificar palabras que visualmente
    /// están en la misma línea pero difieren por pequeños decimales en su renderizado.
    /// </summary>
    private static List<StatementLine> ExtractPositionalRows(IEnumerable<PdfPage> pages)
    {
        var rows = new List<StatementLine>();

        foreach (var page in pages)
        {
            var words = page.GetWords().ToList();
            if (words.Count == 0) continue;

            // Agrupa aplicando la tolerancia de 3.0
            var byY = words
                .GroupBy(w => Math.Round(w.BoundingBox.Bottom / 3.0) * 3.0)
                .OrderByDescending(g => g.Key);

            foreach (var group in byY)
            {
                var cells = group
                    .OrderBy(w => w.BoundingBox.Left)
                    .Select(w => new StatementToken(w.BoundingBox.Left, w.BoundingBox.Right, w.Text.Trim()))
                    .Where(c => !string.IsNullOrWhiteSpace(c.Text))
                    .ToList();

                if (cells.Count > 0)
                {
                    rows.Add(new StatementLine(page.Number, group.Key, cells));
                }
            }
        }

        return rows;
    }

    /// <summary>
    /// Detecta el banco desde el texto ya extraído (usado en el path OCR donde no hay Pages de PdfPig).
    /// </summary>
    private static string DetectBankFromRows(List<StatementLine> rows, string fileName)
    {
        var normalizedFileName = (fileName ?? string.Empty).ToUpperInvariant()
            .Replace("_20", " ").Replace("%20", " ");

        if (normalizedFileName.Contains("BBVA"))        return BankCodes.Bbva;
        if (normalizedFileName.Contains("GALICIA"))     return BankCodes.Galicia;
        if (normalizedFileName.Contains("CREDICOOP"))   return BankCodes.Credicoop;
        if (normalizedFileName.Contains("CIUDAD"))      return BankCodes.Ciudad;
        if (normalizedFileName.Contains("MERCADOPAGO") ||
            normalizedFileName.Contains("_MP_"))        return BankCodes.MercadoPago;

        var text = string.Join(" ",
            rows.Take(80).SelectMany(r => r.Cells.Select(c => c.Text))
        ).ToUpperInvariant();

        if (text.Contains("BBVA") || text.Contains("CREANDO OPORTUNIDADES")) return BankCodes.Bbva;
        if (text.Contains("GALICIA"))                                          return BankCodes.Galicia;
        if (text.Contains("CREDICOOP") || text.Contains("COOPERATIVA DE CRED")) return BankCodes.Credicoop;
        if (text.Contains("MERCADO PAGO") || text.Contains("MERCADOPAGO"))    return BankCodes.MercadoPago;
        if (text.Contains("3-029-") || text.Contains("BANCO CIUDAD"))         return BankCodes.Ciudad;

        return BankCodes.Generic;
    }

    private static string GetTessDataPath()
    {
        var envPath = Environment.GetEnvironmentVariable("TESSDATA_PREFIX");
        if (!string.IsNullOrEmpty(envPath) && Directory.Exists(envPath))
            return envPath;

        // Linux estándar (apt install tesseract-ocr-spa)
        if (Directory.Exists("/usr/share/tessdata"))
            return "/usr/share/tessdata";

        // Bundled junto al exe (Windows dev)
        var appPath = Path.Combine(AppContext.BaseDirectory, "tessdata");
        if (Directory.Exists(appPath))
            return appPath;

        return "./tessdata";
    }

    /// <summary>
    /// Renderiza cada página del PDF como imagen con PDFtoImage (PDFium),
    /// aplica Tesseract OCR y reconstruye la grilla posicional de StatementLine/StatementToken
    /// compatible con el parser existente.
    /// </summary>
    private static List<StatementLine> ExtractRowsViaOcr(Stream pdfStream)
    {
        var tessDataPath = GetTessDataPath();
        var allRows = new List<StatementLine>();

        // spa+eng: español primero, inglés como fallback para números y siglas
        using var engine = new TesseractEngine(tessDataPath, "spa+eng", EngineMode.LstmOnly);

        int pageNum = 0;
#pragma warning disable CA1416 // Se sabe que esto corre en Windows o contenedores permitidos
        // leaveOpen: true — el stream lo posee y cierra el caller (Extract), no este método.
        foreach (var bitmap in Conversion.ToImages(pdfStream, leaveOpen: true, options: new RenderOptions(Dpi: OcrDpi)))
#pragma warning restore CA1416
        {
            pageNum++;
            try
            {
                using (bitmap)
                {
                    using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
                    using var pix     = Pix.LoadFromMemory(encoded.ToArray());
                    using var ocrPage = engine.Process(pix);

                    var wordsByY = new Dictionary<int, List<StatementToken>>();

                    using var iter = ocrPage.GetIterator();
                    iter.Begin();
                    do
                    {
                        if (!iter.TryGetBoundingBox(PageIteratorLevel.Word, out var bounds)) continue;
                        var text = iter.GetText(PageIteratorLevel.Word)?.Trim();
                        if (string.IsNullOrWhiteSpace(text)) continue;

                        // Agrupar palabras por fila con tolerancia de 5 px
                        // En Tesseract Y=0 es la parte superior, usamos Y1 (top del bbox)
                        int yKey = (int)(Math.Round(bounds.Y1 / 5.0) * 5);
                        if (!wordsByY.TryGetValue(yKey, out var cells))
                            wordsByY[yKey] = cells = [];

                        cells.Add(new StatementToken(bounds.X1, bounds.X2, text));
                    }
                    while (iter.Next(PageIteratorLevel.Word));

                    // Ordenar filas de arriba hacia abajo (Y ascendente en Tesseract)
                    foreach (var (yKey, cells) in wordsByY.OrderBy(kvp => kvp.Key))
                    {
                        var sorted = cells.OrderBy(c => c.X).ToList();
                        // Negamos yKey para que el parser (que espera Y descendente de PdfPig) lo procese bien
                        allRows.Add(new StatementLine(pageNum, -yKey, sorted));
                    }
                }
            }
            catch { /* página ilegible, continuar con las demás */ }
        }

        return allRows;
    }
}
