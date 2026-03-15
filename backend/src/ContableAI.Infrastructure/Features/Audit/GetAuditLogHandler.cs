using ContableAI.Application.Common;
using ContableAI.Application.Features.Audit.Queries;
using ContableAI.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace ContableAI.Infrastructure.Features.Audit;

public sealed class GetAuditLogHandler
    : IRequestHandler<GetAuditLogQuery, Result<AuditLogResponse>>
{
    private readonly ContableAIDbContext _db;
    public GetAuditLogHandler(ContableAIDbContext db) => _db = db;

    public async Task<Result<AuditLogResponse>> Handle(GetAuditLogQuery q, CancellationToken ct)
    {
        var page     = Math.Max(1, q.Page);
        var pageSize = Math.Clamp(q.PageSize, 1, 200);

        var query = _db.AuditLogs
            .AsNoTracking()
            .Where(a => a.TenantId == q.StudioTenantId);

        if (!string.IsNullOrWhiteSpace(q.EntityName))
            query = query.Where(a => a.EntityName == q.EntityName);

        if (!string.IsNullOrWhiteSpace(q.Action))
            query = query.Where(a => a.Action == q.Action);

        if (!string.IsNullOrWhiteSpace(q.UserId))
            query = query.Where(a => a.UserId == q.UserId || a.UserEmail.Contains(q.UserId));

        var total = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(a => a.Timestamp)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(a => new AuditLogItem(
                a.Id,
                a.Timestamp,
                a.UserEmail,
                a.Action,
                a.EntityName,
                a.EntityId,
                a.Changes))
            .ToListAsync(ct);

        return Result<AuditLogResponse>.Success(new AuditLogResponse(total, page, pageSize, items));
    }
}
