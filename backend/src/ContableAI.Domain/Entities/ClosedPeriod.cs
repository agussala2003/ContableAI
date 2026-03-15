namespace ContableAI.Domain.Entities;

/// <summary>
/// Representa un período contable cerrado para un estudio.
/// Un período cerrado bloquea la creación/eliminación de asientos
/// y la reclasificación de transacciones pertenecientes a ese mes/año.
/// </summary>
public class ClosedPeriod
{
    public int    Id             { get; private set; }
    public string StudioTenantId { get; init; } = string.Empty;
    public int    Year           { get; init; }
    public int    Month          { get; init; }
    public DateTime ClosedAt     { get; private set; } = DateTime.UtcNow;
    public string ClosedByEmail  { get; init; } = string.Empty;
}
