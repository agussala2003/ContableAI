using ContableAI.Application.Common;
using ContableAI.Application.Features.JournalEntries.Queries;
using ContableAI.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Text;

namespace ContableAI.Infrastructure.Features.JournalEntries;

public sealed class ExportJournalEntriesHandler
    : IRequestHandler<ExportJournalEntriesQuery, Result<CsvFileResult>>
{
    private readonly ContableAIDbContext _db;

    public ExportJournalEntriesHandler(ContableAIDbContext db) => _db = db;

    public async Task<Result<CsvFileResult>> Handle(
        ExportJournalEntriesQuery query,
        CancellationToken         ct)
    {
        var company = await _db.Companies.FindAsync([query.CompanyId], ct);
        if (company == null)
            return Result<CsvFileResult>.NotFound("Empresa no encontrada.");

        var dbQuery = _db.JournalEntries
            .AsNoTracking()
            .Include(j => j.Lines)
            .Where(j => j.CompanyId == query.CompanyId);

        if (query.Month.HasValue && query.Year.HasValue)
        {
            var start = new DateOnly(query.Year.Value, query.Month.Value, 1);
            var end   = start.AddMonths(1).AddDays(-1);
            dbQuery = dbQuery.Where(j => j.Date >= start && j.Date <= end);
        }
        else if (query.Year.HasValue)
        {
            var start = new DateOnly(query.Year.Value, 1, 1);
            var end   = new DateOnly(query.Year.Value, 12, 31);
            dbQuery = dbQuery.Where(j => j.Date >= start && j.Date <= end);
        }
        else if (query.Month.HasValue)
        {
            dbQuery = dbQuery.Where(j => j.Date.Month == query.Month.Value);
        }

        var entries = await dbQuery.OrderBy(j => j.Date).ThenBy(j => j.GeneratedAt).ToListAsync(ct);

        if (entries.Count == 0)
            return Result<CsvFileResult>.NotFound("No hay asientos para el período seleccionado.");

        // ── Build CSV ──────────────────────────────────────────────────────────
        static string Esc(string? value)
        {
            if (string.IsNullOrEmpty(value)) return "\"\"";
            return value.Contains(',') || value.Contains('"') || value.Contains('\n')
                ? $"\"{value.Replace("\"", "\"\"")}\""
                : value;
        }

        var sb = new StringBuilder();
        sb.AppendLine("Fecha,Asiento Nro,Concepto,Cuenta,Debe,Haber");

        int entryNumber = 1;
        foreach (var entry in entries)
        {
            var fecha = entry.Date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
            var concepto = Esc(entry.Description);

            // Lines are sorted: debits first, then credits (standard double-entry presentation)
            var sortedLines = entry.Lines.OrderByDescending(l => l.IsDebit).ThenBy(l => l.Account);
            foreach (var line in sortedLines)
            {
                var debe  = line.IsDebit  ? line.Amount.ToString("0.00", CultureInfo.InvariantCulture) : "";
                var haber = !line.IsDebit ? line.Amount.ToString("0.00", CultureInfo.InvariantCulture) : "";
                sb.AppendLine(string.Join(",",
                    fecha,
                    entryNumber.ToString(CultureInfo.InvariantCulture),
                    concepto,
                    Esc(line.Account),
                    debe,
                    haber));
            }
            entryNumber++;
        }

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());

        var dateLabel = query.Month.HasValue && query.Year.HasValue
            ? $"{query.Month:D2}-{query.Year}"
            : query.Year.HasValue ? $"{query.Year}" : "todo";
        var fileName = $"Asientos_{company.Name.Replace(" ", "_")}_{dateLabel}.csv";

        return Result<CsvFileResult>.Success(new CsvFileResult(bytes, fileName, "text/csv; charset=utf-8"));
    }
}
