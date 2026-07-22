using ContableAI.Application.Common;
using MediatR;

namespace ContableAI.Application.Features.Admin.Commands;

/// <summary>
/// Cierre formal de cuenta de un estudio contable (derecho al olvido — Ley 25.326 / GDPR, P-1).
///
/// Elimina de forma transaccional TODOS los datos del tenant: usuarios, refresh tokens,
/// empresas (activas e inactivas), transacciones bancarias, asientos y sus líneas, comprobantes
/// AFIP, reglas (de empresa y de estudio), sugerencias, plan de cuentas propio, períodos
/// cerrados y resultados de jobs de subida. Los <c>AuditLogs</c> NO se borran (trazabilidad
/// financiera legal): se seudonimizan — email reemplazado por
/// <c>deleted-user-{userId}@anonymized.local</c> y diffs (<c>Changes</c>) vaciados.
/// La ejecución queda registrada con un AuditLog final que documenta quién la pidió y qué se borró.
/// </summary>
/// <param name="StudioTenantId">Tenant del estudio a cerrar.</param>
/// <param name="RequestedBy">Email del SystemAdmin (o flujo auditado) que ejecuta el cierre.</param>
/// <param name="Reason">Motivo declarado del pedido (se persiste en el AuditLog de cierre).</param>
public record DeleteStudioTenantCommand(
    string  StudioTenantId,
    string  RequestedBy,
    string? Reason = null)
    : IRequest<Result<DeleteStudioTenantResponse>>;

/// <summary>Conteo por tabla de lo eliminado/seudonimizado — evidencia del cierre para auditoría.</summary>
public record DeleteStudioTenantResponse(
    string StudioTenantId,
    int UsersDeleted,
    int RefreshTokensDeleted,
    int CompaniesDeleted,
    int BankTransactionsDeleted,
    int JournalEntriesDeleted,
    int JournalEntryLinesDeleted,
    int AccountingRulesDeleted,
    int RuleSuggestionsDeleted,
    int AfipVouchersDeleted,
    int ChartOfAccountsDeleted,
    int ClosedPeriodsDeleted,
    int UploadJobResultsDeleted,
    int StagedFilesPurged,
    int AuditLogsAnonymized);
