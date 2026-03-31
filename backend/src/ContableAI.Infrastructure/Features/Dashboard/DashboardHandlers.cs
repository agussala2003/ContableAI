using ContableAI.Application.Common;
using ContableAI.Application.Features.Dashboard.Queries;
using ContableAI.Domain.Constants;
using ContableAI.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace ContableAI.Infrastructure.Features.Dashboard;

public sealed class GetDashboardStatsHandler
    : IRequestHandler<GetDashboardStatsQuery, Result<DashboardStatsResponse>>
{
    private readonly ContableAIDbContext _db;

    public GetDashboardStatsHandler(ContableAIDbContext db) => _db = db;

    public async Task<Result<DashboardStatsResponse>> Handle(
        GetDashboardStatsQuery query,
        CancellationToken      ct)
    {
        var now   = DateTime.UtcNow;
        var month = query.Month ?? now.Month;
        var year  = query.Year  ?? now.Year;

        // Single-pass: project only the two columns needed, then aggregate in memory.
        // EF Core translates this to: SELECT "ClassificationSource", "ConfidenceScore"
        // WHERE CompanyId = @p AND EXTRACT(month FROM Date) = @m AND EXTRACT(year FROM Date) = @y
        var rows = await _db.BankTransactions
            .AsNoTracking()
            .Where(t =>
                t.CompanyId == query.CompanyId &&
                t.Date.Month == month          &&
                t.Date.Year  == year)
            .Select(t => new
            {
                t.ClassificationSource,
                t.ConfidenceScore,
            })
            .ToListAsync(ct);

        var total   = rows.Count;
        var pending = rows.Count(r => r.ClassificationSource == ClassificationSources.Pending);
        var classified    = total - pending;
        var lowConfidence = rows.Count(r =>
            r.ClassificationSource != ClassificationSources.Pending &&
            r.ConfidenceScore < 0.5f);

        return Result<DashboardStatsResponse>.Success(new DashboardStatsResponse(
            TotalTransactions:    total,
            PendingClassification: pending,
            Classified:           classified,
            LowConfidence:        lowConfidence,
            Month:                month,
            Year:                 year
        ));
    }
}
