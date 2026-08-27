namespace ContableAI.Infrastructure.Services;

/// <summary>
/// Modelo posicional de un extracto bancario, independiente de la fuente física (PDF digital,
/// OCR o fixture de texto en tests). Es el contrato entre la lectura física
/// (<see cref="IStatementTextExtractor"/>) y la interpretación contable (<see cref="PdfBankParser"/>).
/// </summary>

/// <summary>Un token (palabra) posicionado dentro de una línea: su texto y su rango horizontal X.</summary>
internal sealed record StatementToken(double X, double Right, string Text);

/// <summary>
/// Una línea del extracto: los tokens que caen en la misma coordenada vertical, ordenados por X.
/// <paramref name="Y"/> es descendente (convención de PdfPig: mayor Y = más arriba en la página).
/// </summary>
internal sealed record StatementLine(int PageNumber, double Y, List<StatementToken> Cells);

/// <summary>Cómo se leyó el extracto: texto digital embebido o reconstrucción por OCR.</summary>
internal enum StatementSource
{
    Digital,
    Ocr,
}

/// <summary>
/// Resultado de leer físicamente un extracto: sus líneas posicionadas, el banco detectado y
/// cómo se obtuvo (digital/OCR). La interpretación contable trabaja únicamente sobre esto, sin
/// conocer PdfPig, Tesseract ni el formato del archivo.
/// </summary>
internal sealed record StatementDocument(string Bank, StatementSource Source, IReadOnlyList<StatementLine> Lines);

/// <summary>
/// Abstracción de la lectura física de un extracto (Principio de Inversión de Dependencias):
/// convierte un stream de extracto en su modelo posicional + banco detectado. La implementación
/// real (<c>OcrStatementExtractor</c>) encapsula PdfPig y Tesseract; en tests se inyecta un
/// extractor que lee fixtures de texto, de modo que la lógica de interpretación se puede probar
/// sin PDFs reales.
///
/// Es síncrono a propósito: la extracción es CPU-bound (render + OCR) y el pipeline de subida
/// que la consume es síncrono; envolver en <c>Task</c> sería sync-over-async sin beneficio real.
/// </summary>
internal interface IStatementTextExtractor
{
    /// <summary>
    /// Lee el extracto y devuelve sus líneas posicionadas junto al banco detectado. Puede lanzar
    /// <see cref="System.InvalidOperationException"/> con un mensaje apto para el usuario (archivo
    /// demasiado grande, OCR no disponible, o PDF escaneado ilegible).
    /// </summary>
    StatementDocument Extract(Stream statementStream, string fileName);
}
