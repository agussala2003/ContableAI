using ContableAI.Application.Common;
using MediatR;

namespace ContableAI.Application.Features.Audit.Queries;

public record GetAuditLogQuery(
    string  StudioTenantId,
    string? EntityName,
    string? Action,
    string? UserId,
    int     Page,
    int     PageSize)
    : IRequest<Result<AuditLogResponse>>;

public record AuditLogResponse(
    int               Total,
    int               Page,
    int               PageSize,
    List<AuditLogItem> Items);

public record AuditLogItem(
    Guid     Id,
    DateTime Timestamp,
    string   UserEmail,
    string   Action,
    string   EntityName,
    string   EntityId,
    string?  Changes);
