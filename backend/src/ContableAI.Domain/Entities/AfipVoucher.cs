using System;
using ContableAI.Domain.Constants;

namespace ContableAI.Domain.Entities;

public class AfipVoucher : ITenantEntity
{
    public Guid Id { get; private set; } = Guid.NewGuid();

    // Requerido por ITenantEntity (multi-inquilino legacy)
    public string TenantId { get; set; } = string.Empty;

    // Multi-tenancy real (Estudio -> Empresa)
    public string? StudioTenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Company Company { get; set; } = null!;

    public DateOnly Date { get; set; }
    public decimal Amount { get; set; }
    public string TaxName { get; set; } = string.Empty;

    /// <summary>
    /// Moneda del VEP (código ISO 4217, ver <see cref="Currencies"/>). Los VEPs de ARCA se
    /// pagan siempre en pesos; se modela en datos para que el guard de conciliación multi-moneda
    /// no dependa de un supuesto implícito. Default <see cref="Currencies.Ars"/>.
    /// </summary>
    public string Currency { get; set; } = Currencies.Ars;

    public bool IsMatched { get; set; } = false;
    public Guid? MatchedTransactionId { get; set; }
}
