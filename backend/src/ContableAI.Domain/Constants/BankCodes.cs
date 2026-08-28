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

    /// <summary>
    /// Normaliza un código a la forma canónica del catálogo, o <c>null</c> si no es reconocido.
    /// Los códigos se guardaron siempre en mayúsculas, pero el valor puede llegar de una query
    /// string escrita a mano.
    /// </summary>
    public static string? Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;

        var upper = code.Trim().ToUpperInvariant();
        return All.Contains(upper) ? upper : null;
    }

    /// <summary>
    /// Código de entidad del BCRA (los 3 primeros dígitos del CBU) → banco emisor.
    ///
    /// Es la señal de MAYOR confianza para saber de quién es un extracto: el CBU del encabezado
    /// pertenece, por definición, a la cuenta que el documento informa, y su prefijo es el código
    /// de la entidad que la emitió. A diferencia de buscar el nombre del banco en el texto, no la
    /// puede disparar la descripción de un movimiento — una transferencia recibida "de mercado
    /// pago" no convierte un extracto de Santander en uno de MercadoPago.
    ///
    /// Solo entidades bancarias a propósito: el prefijo 000 identifica a un CVU de billetera
    /// virtual y NO distingue MercadoPago de Ualá, Brubank o cualquier otra. Esas se siguen
    /// detectando por nombre, que en sus extractos sí aparece como texto.
    /// </summary>
    private static readonly Dictionary<string, string> ByCbuPrefix = new()
    {
        ["007"] = Galicia,
        ["017"] = Bbva,
        ["029"] = Ciudad,
        ["072"] = Santander,
        ["191"] = Credicoop,
    };

    /// <summary>
    /// Banco emisor deducido de un CBU de 22 dígitos, o <c>null</c> si el prefijo no corresponde a
    /// ninguna entidad del catálogo (billeteras virtuales incluidas: ver <see cref="ByCbuPrefix"/>).
    /// </summary>
    public static string? FromCbu(string? cbu) =>
        cbu is { Length: 22 } && cbu.All(char.IsDigit) && ByCbuPrefix.TryGetValue(cbu[..3], out var bank)
            ? bank
            : null;

    /// <summary>
    /// Nombre del banco tal como lo lee el usuario. Vive junto al catálogo para que agregar un
    /// banco sea un solo cambio: el código y su etiqueta no pueden quedar desalineados.
    /// Un código desconocido se devuelve tal cual — es preferible mostrar el código crudo que
    /// esconder una cuenta cuyo banco quedó fuera del catálogo.
    /// </summary>
    public static string DisplayName(string? code) => code switch
    {
        Bbva        => "BBVA",
        Galicia     => "Banco Galicia",
        Santander   => "Banco Santander",
        Credicoop   => "Banco Credicoop",
        MercadoPago => "MercadoPago",
        Ciudad      => "Banco Ciudad",
        _           => code ?? string.Empty,
    };
}
