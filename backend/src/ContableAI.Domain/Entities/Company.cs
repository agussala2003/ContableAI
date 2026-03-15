namespace ContableAI.Domain.Entities;

/// <summary>
/// Empresa cliente gestionada por un estudio contable.
/// Cada empresa tiene sus propias transacciones bancarias, reglas de clasificación y asientos contables.
/// </summary>
public class Company
{
    public Guid Id { get; private set; } = Guid.NewGuid();

    /// <summary>Nombre del contribuyente (ej: "Emprendimientos Gourmet SRL").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>CUIT del contribuyente; clave para el cruce con presentaciones AFIP.</summary>
    public string Cuit { get; set; } = string.Empty;

    /// <summary>
    /// Tipo de negocio que condiciona la clasificación de cuentas.
    /// Valores típicos: "RESTAURANTE", "PELUQUERIA", "COMERCIO", "GENERAL".
    /// </summary>
    public string BusinessType { get; set; } = "GENERAL";

    public bool IsActive { get; set; } = true;

    /// <summary>Multi-tenant: identifica al estudio contable propietario de esta empresa.</summary>
    public string StudioTenantId { get; set; } = "ESTUDIO_DEFAULT";

    /// <summary>
    /// Cuando es <c>true</c>, el asiento de Impuesto al Cheque se genera con dos líneas:
    /// 50% Impuesto a los Débitos Bancarios / 50% Impuesto a los Créditos Bancarios.
    /// </summary>
    public bool SplitChequeTax { get; set; } = false;

    /// <summary>
    /// Nombre de la cuenta bancaria en el plan de cuentas del estudio (ej: "BBVA - CTA CTE Pesos").
    /// Se usa como contrapartida en los asientos de doble entrada al generar el libro diario.
    /// </summary>
    public string BankAccountName { get; set; } = string.Empty;
}
