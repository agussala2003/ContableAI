using ContableAI.Domain.Constants;

namespace ContableAI.Domain.Entities;

/// <summary>
/// Cuenta bancaria de una empresa. Reemplaza a los campos sueltos
/// <see cref="Company.BankAccountName"/> / <see cref="Company.UsdBankAccountName"/>, que solo
/// permitían una cuenta en pesos y una en dólares por empresa.
///
/// Cumple dos funciones:
///   · <b>Contable</b>: <see cref="ContraAccountName"/> es la contrapartida de los asientos
///     generados a partir de los movimientos de esta cuenta.
///   · <b>Operativa</b>: <see cref="NormalizedNumber"/> permite que el OCR enrute cada extracto
///     a su cuenta, de modo que se puedan subir varios extractos mezclados en una sola carga.
/// </summary>
public class BankAccount
{
    public Guid Id { get; private set; } = Guid.NewGuid();

    /// <summary>Empresa propietaria.</summary>
    public Guid CompanyId { get; set; }
    public Company? Company { get; set; }

    /// <summary>Nombre con el que el contador identifica la cuenta (ej: "Galicia CC $ — Operativa").</summary>
    public string Alias { get; set; } = string.Empty;

    /// <summary>Número de cuenta tal como figura en el extracto, con su formato original.</summary>
    public string? AccountNumber { get; set; }

    /// <summary>
    /// Solo los dígitos de <see cref="AccountNumber"/>, para comparar contra lo que lee el OCR sin
    /// depender de guiones, barras ni espacios.
    ///
    /// Es NULLABLE a propósito: una cuenta puede existir sin conocerse todavía su número (las que
    /// crea el backfill a partir de los campos legacy, y las que se dan de alta a mano antes de
    /// subir el primer extracto). Postgres considera distintos a los NULL en un índice único, así
    /// que varias cuentas sin número conviven sin romper la unicidad por empresa.
    /// </summary>
    public string? NormalizedNumber { get; set; }

    /// <summary>CBU/CVU de 22 dígitos, cuando el extracto lo informa.</summary>
    public string? Cbu { get; set; }

    /// <summary>Banco emisor (ver <c>BankCodes</c>: BBVA, GALICIA, CREDICOOP, ...).</summary>
    public string? BankCode { get; set; }

    /// <summary>Moneda de la cuenta (código ISO 4217, ver <see cref="Currencies"/>).</summary>
    public string Currency { get; set; } = Currencies.Ars;

    /// <summary>
    /// Cuenta contable de contrapartida en los asientos (ej: "Banco Galicia CC $"). Se guarda como
    /// texto y no solo como FK porque todo el motor de asientos trabaja con nombres de cuenta
    /// (<see cref="JournalEntryLine.Account"/>, <see cref="AccountingRule.TargetAccount"/>).
    /// Vacía = cuenta provisional: existe y recibe movimientos, pero todavía no se puede asentar.
    /// </summary>
    public string ContraAccountName { get; set; } = string.Empty;

    /// <summary>
    /// Cuenta del plan de cuentas equivalente a <see cref="ContraAccountName"/>, cuando existe.
    /// Permite resolver el código externo (Tango/Holistor/Bejerman) sin buscar por nombre.
    /// </summary>
    public Guid? ChartOfAccountId { get; set; }

    /// <summary>
    /// Baja lógica. Nunca se borra una cuenta con historia: los movimientos y asientos ya
    /// generados siguen apuntando a ella.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Estudio propietario, DESNORMALIZADO desde <see cref="Company.StudioTenantId"/> — mismo
    /// patrón que <see cref="BankTransaction.StudioTenantId"/> y <see cref="AccountingRule.StudioTenantId"/>.
    /// Es el ancla del Global Query Filter, que así no necesita joinear a Companies.
    /// </summary>
    public string StudioTenantId { get; set; } = string.Empty;
}
