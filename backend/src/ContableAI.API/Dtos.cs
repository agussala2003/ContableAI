record UpdateAccountRequest(string AssignedAccount);
record BulkUpdateRequest(List<Guid> Ids, string AssignedAccount, Guid? RuleId = null);
record CreateCompanyRequest(string Name, string Cuit, string? BusinessType, string? BankAccountName);
record UpdateCompanyRequest(string? Name, string? BusinessType, bool? SplitChequeTax, string? BankAccountName);
record CreateRuleRequest(
    string Keyword,
    string TargetAccount,
    string? Direction,          // "DEBIT", "CREDIT" o null
    int?   Priority,
    bool?  RequiresTaxMatching
);
record CreateChartOfAccountRequest(string Name, string? ExternalCode = null);
record UpdateChartOfAccountRequest(string? ExternalCode);
record GenerateJournalEntriesRequest(List<Guid> TransactionIds);
record ClosePeriodRequest(int Year, int Month);
