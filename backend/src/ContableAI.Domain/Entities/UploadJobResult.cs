namespace ContableAI.Domain.Entities;

/// <summary>
/// Resultado durable de un job de procesamiento de extracto bancario (ver
/// <c>UploadBankStatementHandler</c>), escrito al terminar el job y leído por el endpoint de
/// polling <c>GET /api/transactions/upload/{uploadId}/result</c>. La clave es el <c>Guid</c> que
/// el propio endpoint de subida genera al encolar (no el JobId interno de Hangfire).
/// </summary>
public class UploadJobResult
{
    public string   JobId          { get; set; } = string.Empty;
    public string   StudioTenantId { get; set; } = string.Empty;
    public bool     IsSuccess      { get; set; }
    public int      StatusCode     { get; set; }

    /// <summary>JSON de <c>UploadBankStatementResponse</c> (éxito) o del mensaje de error (fallo).</summary>
    public string   ResultJson     { get; set; } = string.Empty;
    public DateTime CreatedAt      { get; set; } = DateTime.UtcNow;
}
