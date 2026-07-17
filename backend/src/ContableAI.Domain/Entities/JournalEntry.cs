using ContableAI.Domain.Constants;

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

    /// <summary>
    /// Moneda del asiento (código ISO 4217, ver <see cref="Currencies"/>). Se denormaliza desde
    /// la <see cref="BankTransaction"/> de origen a propósito: el libro diario se filtra y
    /// exporta sin joinear a la transacción, y un asiento nunca mezcla monedas.
    /// Default <see cref="Currencies.Ars"/>.
    /// </summary>
    public string Currency { get; init; } = Currencies.Ars;

    public List<JournalEntryLine> Lines { get; init; } = [];
}
