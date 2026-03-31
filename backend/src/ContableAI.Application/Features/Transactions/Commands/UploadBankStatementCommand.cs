using ContableAI.Application.Common;
using MediatR;

namespace ContableAI.Application.Features.Transactions.Commands;

/// <summary>Represents a single uploaded file, detached from any HTTP abstraction.</summary>
public sealed record FileData(byte[] Content, string FileName, long Length);

/// <summary>Per-file result included in the upload response.</summary>
public sealed record FileUploadResult(string FileName, int Processed, int DuplicatesSkipped);

/// <summary>
/// Command to parse, classify, deduplicate and persist one or more bank statement files.
/// </summary>
public sealed record UploadBankStatementCommand(
    IReadOnlyList<FileData> Files,
    Guid?   CompanyId,
    string? BankCode
) : IRequest<Result<UploadBankStatementResponse>>;

public sealed record UploadBankStatementResponse(
    int TotalFiles,
    int TotalProcessed,
    int DuplicatesSkipped,
    string CompanyName,
    IReadOnlyList<FileUploadResult> PerFile
);
