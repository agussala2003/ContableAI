using System;

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

    public bool IsMatched { get; set; } = false;
    public Guid? MatchedTransactionId { get; set; }
}
