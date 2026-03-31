using ContableAI.Application.Common;
using MediatR;

namespace ContableAI.Application.Features.Dashboard.Queries;

/// <summary>
/// Retorna los KPIs de conciliación del mes en curso para una empresa.
/// </summary>
/// <param name="CompanyId">ID de la empresa seleccionada (requerido).</param>
/// <param name="Month">Mes a consultar. Si es null, usa el mes actual.</param>
/// <param name="Year">Año a consultar. Si es null, usa el año actual.</param>
public sealed record GetDashboardStatsQuery(
    Guid  CompanyId,
    int?  Month = null,
    int?  Year  = null
) : IRequest<Result<DashboardStatsResponse>>;

/// <summary>KPIs de conciliación para el mes solicitado.</summary>
public sealed record DashboardStatsResponse(
    int   TotalTransactions,
    int   PendingClassification,
    int   Classified,
    int   LowConfidence,
    int   Month,
    int   Year
);
