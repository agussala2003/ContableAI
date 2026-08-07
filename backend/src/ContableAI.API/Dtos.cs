record UpdateAccountRequest(string AssignedAccount);
record BulkUpdateRequest(List<Guid> Ids, string AssignedAccount, Guid? RuleId = null);
record CreateCompanyRequest(string Name, string Cuit, string? BusinessType, string? BankAccountName, string? UsdBankAccountName = null);
record UpdateCompanyRequest(string? Name, string? BusinessType, bool? SplitChequeTax, string? BankAccountName, string? UsdBankAccountName = null);
record CreateRuleRequest(
    string Keyword,
    string TargetAccount,
    string? Direction,          // "DEBIT", "CREDIT" o null
    int?   Priority,
    bool?  RequiresTaxMatching
);
/// <summary>
/// Regla dentro de un archivo de exportación. Deliberadamente NO lleva Id, CompanyId ni
/// StudioTenantId: el archivo describe la configuración, no las filas de una base concreta, y
/// aceptarlos permitiría escribir en una empresa ajena mandando un JSON armado a mano.
/// </summary>
record ImportRuleItem(
    string  Keyword,
    string  TargetAccount,
    string? Direction,
    int?    Priority,
    bool?   RequiresTaxMatching
);

record ImportRulesRequest(List<ImportRuleItem> Rules);

record CreateChartOfAccountRequest(string Name, string? ExternalCode = null);

/// <summary>Alta y edición de una cuenta bancaria de empresa (F1: multi-cuenta).</summary>
record SaveBankAccountRequest(
    string  Alias,
    string? AccountNumber,
    string? Cbu,
    string? BankCode,
    string  Currency,
    string? ContraAccountName,
    Guid?   ChartOfAccountId = null);
record UpdateChartOfAccountRequest(string? ExternalCode);
record GenerateJournalEntriesRequest(List<Guid> TransactionIds);
record ApplyAfipCombinationRequest(Guid TransactionId, List<Guid> VoucherIds);
record ClosePeriodRequest(int Year, int Month);
