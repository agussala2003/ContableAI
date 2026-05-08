using ContableAI.Domain.Constants;

namespace ContableAI.Domain.Entities;

public enum SuggestionStatus
{
    Pending,
    Accepted,
    Rejected
}

/// <summary>
/// Sugerencia proactiva de regla contable, generada a partir de patrones repetitivos (>= 3 veces)
/// realizados manualmente por el usuario.
/// </summary>
public class RuleSuggestion : ITenantEntity
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    
    public string TenantId { get; set; } = "ESTUDIO_DEFAULT";
    public Guid? CompanyId { get; set; }
    
    public string Keyword { get; init; } = string.Empty;
    public string SuggestedAccount { get; set; } = string.Empty;
    
    /// <summary>Cantidad de veces que el usuario realizó esta asignación manual.</summary>
    public int Frequency { get; set; }
    
    public SuggestionStatus Status { get; set; } = SuggestionStatus.Pending;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}
