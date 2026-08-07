using ContableAI.Application.Common;
using MediatR;

namespace ContableAI.Application.Features.Transactions.Commands;

/// <summary>Referencia a un archivo ya subido y guardado en <c>StagedUploadFiles</c> (staging bytea).</summary>
public sealed record StagedFileRef(Guid StagedFileId, string FileName, long Length);

/// <summary>Per-file result included in the upload response.</summary>
public sealed record FileUploadResult(
    string  FileName,
    int     Processed,
    int     DuplicatesSkipped,
    string? BankAccountAlias = null);

/// <summary>
/// Cuenta bancaria que el enrutamiento creó sola, a partir de un número detectado en el extracto
/// que no correspondía a ninguna cuenta existente. Nace provisional (sin contrapartida contable):
/// recibe los movimientos, pero no los puede asentar hasta que el usuario la complete. La UI la
/// usa para ofrecer justamente eso.
/// </summary>
public sealed record CreatedBankAccountItem(Guid Id, string Alias, string? AccountNumber, string Currency);

/// <summary>Details of a single transaction that was skipped as a duplicate.</summary>
public sealed record SkippedDuplicateItem(DateOnly Date, decimal Amount, string Description, string Currency);

/// <summary>
/// Command to parse, classify, deduplicate and persist one or more bank statement files. Se
/// ejecuta dentro de un job de Hangfire (ver <c>TransactionEndpoints.MapTransactionEndpoints</c>),
/// por eso no depende de <c>ICurrentTenantService</c> (no hay HttpContext dentro del job): el
/// tenant y el correlation id del job viajan explícitos en el propio command.
/// </summary>
/// <param name="BankAccountId">
/// Cuenta elegida explícitamente en la Dropzone. Cuando viene, gana sobre la detección automática:
/// el usuario sabe mejor que el OCR a qué cuenta pertenece el archivo que está subiendo.
/// <c>null</c> = modo automático, se enruta por el número detectado en cada extracto.
/// </param>
public sealed record UploadBankStatementCommand(
    Guid    UploadId,
    IReadOnlyList<StagedFileRef> Files,
    Guid?   CompanyId,
    string? BankCode,
    bool    WithoutDateFilter,
    bool    ForceReapplyRules,
    string  StudioTenantId,
    Guid?   BankAccountId = null
) : IRequest<Result<UploadBankStatementResponse>>;

public sealed record UploadBankStatementResponse(
    int TotalFiles,
    int TotalProcessed,
    int DuplicatesSkipped,
    int ReappliedToExisting,
    string CompanyName,
    IReadOnlyList<FileUploadResult> PerFile,
    IReadOnlyList<SkippedDuplicateItem> SkippedDuplicates,
    IReadOnlyList<string> ParseErrors,
    IReadOnlyList<CreatedBankAccountItem> CreatedBankAccounts
);
