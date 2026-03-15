namespace ContableAI.Domain.Entities;

/// <summary>
/// Asiento contable de partida doble generado a partir de una BankTransaction.
/// </summary>
public class JournalEntry
{
    public Guid           Id                 { get; private set; } = Guid.NewGuid();
    public DateOnly       Date               { get; init; }
    public string         Description        { get; init; } = string.Empty;
    public Guid?          CompanyId          { get; init; }
    public Guid           BankTransactionId  { get; init; }
    public DateTime       GeneratedAt        { get; private set; } = DateTime.UtcNow;

    public List<JournalEntryLine> Lines { get; init; } = [];

    /// <summary>Token de concurrencia optimista — EF Core lo actualiza en cada escritura.</summary>
    public byte[] RowVersion { get; private set; } = [];
}
