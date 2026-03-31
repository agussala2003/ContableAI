using ContableAI.Application.Common;
using MediatR;

namespace ContableAI.Application.Features.JournalEntries.Queries;

/// <summary>
/// Exports all journal entries of a company for a given period to a standard
/// accounting CSV file (Fecha, Asiento Nro, Concepto, Cuenta, Debe, Haber).
/// </summary>
public sealed record ExportJournalEntriesQuery(
    Guid  CompanyId,
    int?  Month = null,
    int?  Year  = null
) : IRequest<Result<CsvFileResult>>;

/// <summary>In-memory file ready to be streamed as an HTTP response.</summary>
public sealed record CsvFileResult(
    byte[] Content,
    string FileName,
    string ContentType
);
