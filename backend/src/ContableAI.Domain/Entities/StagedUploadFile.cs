namespace ContableAI.Domain.Entities;

/// <summary>
/// Contenido de un archivo subido, en tránsito entre el encolado del job de procesamiento
/// (<c>POST /api/transactions/upload</c>) y su ejecución en segundo plano (Hangfire). El handler
/// borra la fila apenas termina de leer los bytes, sea cual sea el resultado.
/// </summary>
public class StagedUploadFile
{
    public Guid     Id        { get; set; } = Guid.NewGuid();
    public string   FileName  { get; set; } = string.Empty;
    public byte[]   Content   { get; set; } = [];
    public long     Length    { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
