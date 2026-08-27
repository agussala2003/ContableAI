using ContableAI.Application.Common;
using MediatR;

namespace ContableAI.Application.Features.Dashboard.Queries;

/// <summary>
/// Retorna los KPIs de conciliación del mes en curso para una empresa.
/// </summary>
/// <param name="CompanyId">ID de la empresa seleccionada (requerido).</param>
/// <param name="Month">Mes a consultar. Si es null, usa el mes actual.</param>
/// <param name="Year">Año a consultar. Si es null, usa el año actual.</param>
/// <param name="BankCode">
/// Banco por el que acotar los KPIs (Fase C). Si es null, se cuentan los movimientos de todas las
/// cuentas. El pedido del cliente fue explícito: con tres bancos en una misma empresa, un KPI que
/// los suma no le sirve para revisar el balance de ninguno.
/// </param>
/// <param name="NoBankOnly">
/// Acota a lo que no se puede atribuir a un banco: movimientos sin cuenta, o de cuentas a las que
/// todavía no se les cargó el banco. Va aparte de <paramref name="BankCode"/> porque "sin banco" no
/// es un código: es la ausencia de uno.
/// </param>
public sealed record GetDashboardStatsQuery(
    Guid    CompanyId,
    int?    Month      = null,
    int?    Year       = null,
    string? BankCode   = null,
    bool    NoBankOnly = false
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
