using ContableAI.Domain.Constants;

namespace ContableAI.Infrastructure.Services;

/// <summary>
/// Banco tabular sin particularidades (fallback "GENERIC"): usa el motor base tal cual, sin
/// enriquecimientos ni extracción de id externo. Sirve para extractos con el formato estándar
/// Fecha/Descripción/Débito/Crédito/Saldo de un banco aún no soportado explícitamente.
/// </summary>
internal sealed class GenericStatementParser : TabularStatementParser
{
    public override string Bank => BankCodes.Generic;
}
