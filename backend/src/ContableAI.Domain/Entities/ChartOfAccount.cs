namespace ContableAI.Domain.Entities;

/// <summary>
/// Cuenta del plan de cuentas (catálogo de cuentas contables).
/// Las cuentas globales (<see cref="StudioTenantId"/> = <c>null</c>) son creadas por seed
/// y visibles para todos los estudios; las personalizadas son específicas de un estudio.
/// </summary>
public class ChartOfAccount
{
    public Guid   Id             { get; private set; } = Guid.NewGuid();

    /// <summary>Nombre de la cuenta contable (ej: "Banco BBVA", "AFIP a Determinar").</summary>
    public string Name           { get; set; }  = string.Empty;

    /// <summary>
    /// <c>null</c> = cuenta global (seed, visible para todos los estudios).
    /// <c>Guid</c> = cuenta personalizada del estudio contable.
    /// </summary>
    public Guid? StudioTenantId  { get; set; }  = null;

    /// <summary>
    /// Código de cuenta en el sistema externo del contador (Tango, Bejerman, Oracle, etc.).
    /// Nullable — solo se completa si el estudio lo requiere.
    /// </summary>
    public string? ExternalCode  { get; set; }  = null;

    /// <summary>
    /// Indica si la cuenta está activa. Las cuentas inactivas se ocultan en los selectores
    /// pero conservan sus relaciones históricas (movimientos, asientos, reglas).
    /// </summary>
    public bool IsActive { get; set; } = true;
}
