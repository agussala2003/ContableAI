using ContableAI.Domain.Entities;
using ContableAI.Domain.Enums;

using ContableAI.Domain.Constants;

namespace ContableAI.Infrastructure.Services;

/// <summary>
/// Estrategia de interpretación de un extracto ya leído: recibe las líneas posicionadas
/// (independientes de la fuente física) y produce las transacciones del banco que maneja.
///
/// Cada banco es una implementación separada (Strategy). Agregar un banco nuevo es crear una
/// clase que implemente esta interfaz (o que herede de <see cref="TabularStatementParser"/> si su
/// formato es de tabla débito/crédito/saldo) y registrarla — sin tocar el orquestador ni el resto
/// de los parsers (Open/Closed).
/// </summary>
internal interface IBankStatementParser
{
    /// <summary>Código de banco que maneja esta estrategia (ver <see cref="BankCodes"/>).</summary>
    string Bank { get; }

    /// <summary>Interpreta las líneas del extracto y devuelve las transacciones (sin la moneda,
    /// que estampa el orquestador a nivel documento).</summary>
    IReadOnlyList<BankTransaction> Parse(IReadOnlyList<StatementLine> lines, string? fileName);
}

/// <summary>
/// Ancla de una transacción (posición + fecha + importe + tipo) para parsers que reconstruyen la
/// descripción por proximidad vertical (MercadoPago).
/// </summary>
internal sealed record TxAnchor(int PageNumber, double RowY, DateOnly Date, decimal Amount, TransactionType Type);
