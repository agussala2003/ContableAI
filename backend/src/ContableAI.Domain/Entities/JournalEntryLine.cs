namespace ContableAI.Domain.Entities;

/// <summary>
/// Línea de un asiento contable (partida doble).
/// IsDebit = true → columna Debe; false → columna Haber.
/// </summary>
public class JournalEntryLine
{
    public Guid     Id             { get; private set; } = Guid.NewGuid();
    public Guid     JournalEntryId { get; init; }
    public string   Account        { get; init; } = string.Empty;
    public decimal  Amount         { get; init; }
    public bool     IsDebit        { get; init; }

    public JournalEntry? JournalEntry { get; init; }
}
