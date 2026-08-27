namespace ContableAI.Domain.Constants;

/// <summary>
/// Bancos que el sistema reconoce, identificados por un código corto y estable.
///
/// Vivía como clase <c>internal</c> dentro de Infrastructure, donde solo la veían los parsers.
/// Se promovió al Domain porque el código de banco dejó de ser un detalle del parseo: es el valor
/// que se persiste en <see cref="Entities.BankAccount.BankCode"/> y el que la API va a exponer
/// como filtro. Con una sola definición, el parser que detecta el banco, la cuenta que lo guarda
/// y el endpoint que filtra por él hablan necesariamente del mismo conjunto de valores.
///
/// Se usan strings y no un enum por el mismo motivo que <see cref="Currencies"/>: el valor viaja
/// sin mapeos desde el parser hasta el frontend, y sumar un banco no obliga a migrar datos.
/// </summary>
public static class BankCodes
{
    public const string Bbva        = "BBVA";
    public const string Galicia     = "GALICIA";
    public const string Santander   = "SANTANDER";
    public const string Credicoop   = "CREDICOOP";
    public const string MercadoPago = "MERCADOPAGO";
    public const string Ciudad      = "CIUDAD";

    /// <summary>
    /// Banco no identificado. NO es un banco: es el valor que devuelve la detección cuando no
    /// reconoce el extracto, y el que hace que <c>UploadBankStatementHandler</c> guarde la cuenta
    /// con <c>BankCode = null</c> en lugar de inventar uno.
    /// </summary>
    public const string Generic = "GENERIC";

    /// <summary>
    /// Bancos reconocidos, sin <see cref="Generic"/>. Es el conjunto que corresponde ofrecer como
    /// opción al usuario o aceptar en un filtro; <see cref="Generic"/> queda afuera a propósito.
    /// </summary>
    public static readonly IReadOnlyList<string> All =
    [
        Bbva, Galicia, Santander, Credicoop, MercadoPago, Ciudad,
    ];

    /// <summary>
    /// Indica si el código corresponde a un banco reconocido. Se valida en los puntos de entrada
    /// para impedir que un valor arbitrario llegue a la BD, igual que
    /// <see cref="Currencies.IsSupported"/>.
    /// </summary>
    public static bool IsSupported(string? code) =>
        code is not null && All.Contains(code.Trim().ToUpperInvariant());
}
