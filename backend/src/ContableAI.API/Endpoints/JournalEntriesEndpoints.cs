using ContableAI.API.Common;
using ContableAI.Application.Features.JournalEntries.Commands;
using ContableAI.Application.Features.JournalEntries.Queries;
using ContableAI.Domain.Constants;
using ContableAI.Domain.Entities;
using ContableAI.Domain.Enums;
using ContableAI.Infrastructure.Persistence;
using ContableAI.Infrastructure.Services;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using Hangfire;

namespace ContableAI.API.Endpoints;

public static class JournalEntriesEndpoints
{
    public static void MapJournalEntriesEndpoints(this WebApplication app)
    {

        app.MapPost("/api/journal-entries/generate", (
            GenerateJournalEntriesRequest req,
            ICurrentTenantService          currentTenant,
            IBackgroundJobClient           backgroundJobClient) =>
        {
            if (req.TransactionIds == null || req.TransactionIds.Count == 0)
                return Results.BadRequest("Se requiere al menos una transacción.");

            var command = new GenerateJournalEntriesCommand(req.TransactionIds, currentTenant.StudioTenantId!);
            
            var jobId = backgroundJobClient.Enqueue<ISender>(sender => sender.Send(command, default));

            return Results.Accepted(value: new
            {
                JobId = jobId,
                Message = "Generación de asientos iniciada en segundo plano. Esto puede demorar unos minutos."
            });
        })
        .WithName("GenerateJournalEntries")
        .WithTags("Libro Diario")
        .WithSummary("Generar asientos contables desde transacciones clasificadas.")
        .WithDescription("Body: { transactionIds: [guid] }. Encola el trabajo en Hangfire y devuelve 202 Accepted.")
        .Produces(202)
        .Produces(400);


        app.MapGet("/api/journal-entries", async (
            ICurrentTenantService currentTenant,
            ContableAIDbContext   dbContext,
            [FromQuery] string?   companyId,
            [FromQuery] int?      month,
            [FromQuery] int?      year) =>
        {
            var studioCompanyIds = await dbContext.Companies
                .Where(c => c.StudioTenantId == currentTenant.StudioTenantId && c.IsActive)
                .Select(c => c.Id)
                .ToListAsync();

            var query = dbContext.JournalEntries
                .Where(j => j.CompanyId.HasValue && studioCompanyIds.Contains(j.CompanyId.Value));

            if (!string.IsNullOrWhiteSpace(companyId) && Guid.TryParse(companyId, out var cGuid))
                query = query.Where(j => j.CompanyId == cGuid);

            if (month.HasValue && year.HasValue)
            {
                var start = new DateOnly(year.Value, month.Value, 1);
                var end   = start.AddMonths(1).AddDays(-1);
                query = query.Where(j => j.Date >= start && j.Date <= end);
            }
            else if (year.HasValue)
            {
                var start = new DateOnly(year.Value, 1, 1);
                var end   = new DateOnly(year.Value, 12, 31);
                query = query.Where(j => j.Date >= start && j.Date <= end);
            }

            var entries = await query
                .OrderBy(j => j.Date)
                .Select(j => new
                {
                    j.Id,
                    j.Date,
                    j.Description,
                    j.CompanyId,
                    j.BankTransactionId,
                    j.GeneratedAt,
                    Lines = j.Lines.OrderByDescending(l => l.IsDebit).ThenBy(l => l.Account).Select(l => new { l.Account, l.Amount, l.IsDebit }),
                })
                .ToListAsync();

            return Results.Ok(entries);
        })
        .WithName("GetJournalEntries")
        .WithTags("Libro Diario")
        .WithSummary("Listar asientos del estudio, filtrable por empresa y período.")
        .WithDescription("Query params: companyId (guid), month (int), year (int). Devuelve asientos con sus líneas (account, amount, isDebit). Sin filtros devuelve todos los asientos del estudio.")
        .Produces(200);

        app.MapDelete("/api/journal-entries/{id:guid}", async (
            Guid                  id,
            ICurrentTenantService currentTenant,
            ContableAIDbContext   dbContext) =>
        {
            var entry = await dbContext.JournalEntries.FindAsync(id);
            if (entry == null) return Results.NotFound();

            // JournalEntry no tiene Global Query Filter (no expone navegación a Company), así que
            // validamos la pertenencia al estudio de forma explícita: si el asiento no corresponde
            // a una empresa del tenant autenticado, devolvemos NotFound (fix IDOR de borrado cross-tenant).
            var belongsToStudio = entry.CompanyId.HasValue && await dbContext.Companies
                .AnyAsync(c => c.Id == entry.CompanyId.Value);
            if (!belongsToStudio) return Results.NotFound();

            if (await PeriodEndpoints.IsPeriodClosedAsync(dbContext, currentTenant.StudioTenantId!, entry.Date.Year, entry.Date.Month))
                return Results.Problem(
                    title:      "Período cerrado",
                    detail:     $"El asiento pertenece al período {entry.Date.Month:D2}/{entry.Date.Year} que está cerrado. Reabrilo antes de revertir.",
                    statusCode: 422);

            var linkedTransactions = await dbContext.BankTransactions
                .Where(t => t.JournalEntryId == entry.Id)
                .ToListAsync();

            foreach (var tx in linkedTransactions)
                tx.JournalEntryId = null;

            dbContext.JournalEntries.Remove(entry);
            await dbContext.SaveChangesAsync();
            return Results.NoContent();
        })
        .WithName("DeleteJournalEntry")
        .WithTags("Libro Diario")
        .WithSummary("Revertir un asiento contable.")
        .WithDescription("Elimina el asiento y desvincula la transacción bancaria (JournalEntryId = null), dejando la transacción lista para re-asentar. Valida que el período no esté cerrado.")
        .Produces(204)
        .Produces(404)
        .Produces(422);

        app.MapDelete("/api/journal-entries", async (
            ICurrentTenantService currentTenant,
            IMediator             mediator,
            [FromQuery] string?   companyId,
            [FromQuery] int?      month,
            [FromQuery] int?      year) =>
        {
            if (!string.IsNullOrWhiteSpace(companyId) && !Guid.TryParse(companyId, out _))
                return Results.BadRequest(new { message = "companyId inválido." });

            // Safety: avoid accidental full wipe of all journal entries in the studio.
            if (string.IsNullOrWhiteSpace(companyId) && !month.HasValue && !year.HasValue)
            {
                return Results.BadRequest(new
                {
                    message = "Debés indicar al menos companyId, year o month para borrar masivamente."
                });
            }

            if (month is < 1 or > 12)
                return Results.BadRequest(new { message = "month debe estar entre 1 y 12." });

            if (month.HasValue && !year.HasValue)
                return Results.BadRequest(new { message = "Si enviás month, también debés enviar year." });

            var cmd = new DeleteAllJournalEntriesCommand(
                currentTenant.StudioTenantId!,
                Guid.TryParse(companyId, out var cGuid) ? cGuid : null,
                month,
                year);

            var result = await mediator.Send(cmd);
            return result.ToHttpResult();
        })
        .WithName("DeleteAllJournalEntries")
        .WithTags("Libro Diario")
        .WithSummary("Borrar masivamente asientos contables por alcance filtrado.")
        .WithDescription("Query params: companyId (guid), month (1..12), year. Requiere al menos un filtro de alcance para evitar borrados globales no intencionales. Valida períodos cerrados antes de borrar.")
        .Produces(200)
        .Produces(400)
        .Produces(422);


        app.MapPost("/api/journal-entries/export", async (
            ICurrentTenantService currentTenant,
            ContableAIDbContext   dbContext,
            [FromServices] IExportService exportService,
            ExportJournalEntriesRequest req) =>
        {
            string companyName = "Empresa";
            if (!string.IsNullOrWhiteSpace(req.CompanyId) && Guid.TryParse(req.CompanyId, out var cGuid))
            {
                var co = await dbContext.Companies.FindAsync(cGuid);
                if (co != null) companyName = co.Name;
            }

            var studioCompanies = await dbContext.Companies
                .Where(c => c.StudioTenantId == currentTenant.StudioTenantId && c.IsActive)
                .Select(c => new { c.Id, c.BankAccountName })
                .ToListAsync();

            var studioCompanyIds = studioCompanies.Select(c => c.Id).ToList();

            var query = dbContext.JournalEntries
                .Include(j => j.Lines)
                .Where(j => j.CompanyId.HasValue && studioCompanyIds.Contains(j.CompanyId.Value));

            if (!string.IsNullOrWhiteSpace(req.CompanyId) && Guid.TryParse(req.CompanyId, out var expCmpId))
                query = query.Where(j => j.CompanyId == expCmpId);

            if (req.Month.HasValue && req.Year.HasValue)
            {
                var start = new DateOnly(req.Year.Value, req.Month.Value, 1);
                var end   = start.AddMonths(1).AddDays(-1);
                query = query.Where(j => j.Date >= start && j.Date <= end);
            }
            else if (req.Year.HasValue)
            {
                var start = new DateOnly(req.Year.Value, 1, 1);
                var end   = new DateOnly(req.Year.Value, 12, 31);
                query = query.Where(j => j.Date >= start && j.Date <= end);
            }
            else if (req.Month.HasValue)
            {
                query = query.Where(j => j.Date.Month == req.Month.Value);
            }

            if (!string.IsNullOrWhiteSpace(req.Search))
                query = query.Where(j => j.Description.Contains(req.Search));

            if (!string.IsNullOrWhiteSpace(req.Account))
                query = query.Where(j => j.Lines.Any(l => l.Account == req.Account));

            if (req.EntryIds is { Count: > 0 })
            {
                var ids = req.EntryIds
                    .Select(s => Guid.TryParse(s, out var id) ? id : Guid.Empty)
                    .Where(id => id != Guid.Empty)
                    .Distinct()
                    .ToList();

                if (ids.Count == 0)
                    return Results.BadRequest("entryIds inválido.");

                query = query.Where(j => ids.Contains(j.Id));
            }

            var entries = await query.OrderBy(j => j.Date).ToListAsync();
            if (!entries.Any())
                return Results.NotFound("No hay asientos para el período seleccionado.");

            var balanceAccounts = studioCompanies
                .Where(c => !string.IsNullOrWhiteSpace(c.BankAccountName))
                .Select(c => c.BankAccountName!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            Guid.TryParse(currentTenant.StudioTenantId, out var studioGuidPost);
            var externalCodesPost = await dbContext.ChartOfAccounts
                .AsNoTracking()
                .Where(a => a.ExternalCode != null && (a.StudioTenantId == null || a.StudioTenantId == studioGuidPost))
                .ToDictionaryAsync(a => a.Name, a => a.ExternalCode!);

            var fileBytes = exportService.ExportJournalEntriesToExcel(entries, companyName, req.Month, req.Year, balanceAccounts, externalCodesPost);
            var dateLabel = req.Month.HasValue && req.Year.HasValue
                ? $"{req.Month:D2}-{req.Year}"
                : req.Year.HasValue ? $"{req.Year}" : "todo";
            var fileName  = $"LibroDiario_{companyName.Replace(" ", "_")}_{dateLabel}.xlsx";

            return Results.File(fileBytes,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                fileName);
        })
        .WithName("ExportJournalEntriesPost")
        .WithTags("Libro Diario")
        .WithSummary("Exportar asientos del período a Excel (.xlsx) vía POST.")
        .WithDescription("Body: companyId, month, year, search, account, entryIds[]. Genera el libro diario formateado en Excel para evitar límites de URL en filtros grandes.")
        .Produces(200)
        .Produces(400)
        .Produces(404);


        app.MapGet("/api/journal-entries/export", async (
            ICurrentTenantService currentTenant,
            ContableAIDbContext   dbContext,
            [FromServices] IExportService exportService,
            [FromQuery] string? companyId,
            [FromQuery] int?    month,
            [FromQuery] int?    year,
            [FromQuery] string? search,
            [FromQuery] string? account,
            [FromQuery] string? entryIds) =>
        {
            string companyName = "Empresa";
            if (!string.IsNullOrWhiteSpace(companyId) && Guid.TryParse(companyId, out var cGuid))
            {
                var co = await dbContext.Companies.FindAsync(cGuid);
                if (co != null) companyName = co.Name;
            }

            var studioCompanies = await dbContext.Companies
                .Where(c => c.StudioTenantId == currentTenant.StudioTenantId && c.IsActive)
                .Select(c => new { c.Id, c.BankAccountName })
                .ToListAsync();

            var studioCompanyIds = studioCompanies.Select(c => c.Id).ToList();

            var query = dbContext.JournalEntries
                .Include(j => j.Lines)
                .Where(j => j.CompanyId.HasValue && studioCompanyIds.Contains(j.CompanyId.Value));

            if (!string.IsNullOrWhiteSpace(companyId) && Guid.TryParse(companyId, out var expCmpId))
                query = query.Where(j => j.CompanyId == expCmpId);

            if (month.HasValue && year.HasValue)
            {
                var start = new DateOnly(year.Value, month.Value, 1);
                var end   = start.AddMonths(1).AddDays(-1);
                query = query.Where(j => j.Date >= start && j.Date <= end);
            }
            else if (year.HasValue)
            {
                var start = new DateOnly(year.Value, 1, 1);
                var end   = new DateOnly(year.Value, 12, 31);
                query = query.Where(j => j.Date >= start && j.Date <= end);
            }
            else if (month.HasValue)
            {
                query = query.Where(j => j.Date.Month == month.Value);
            }

            if (!string.IsNullOrWhiteSpace(search))
                query = query.Where(j => j.Description.Contains(search));

            if (!string.IsNullOrWhiteSpace(account))
                query = query.Where(j => j.Lines.Any(l => l.Account == account));

            if (!string.IsNullOrWhiteSpace(entryIds))
            {
                var ids = entryIds
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(s => Guid.TryParse(s, out var id) ? id : Guid.Empty)
                    .Where(id => id != Guid.Empty)
                    .Distinct()
                    .ToList();

                if (ids.Count == 0)
                    return Results.BadRequest("entryIds inválido.");

                query = query.Where(j => ids.Contains(j.Id));
            }

            var entries = await query.OrderBy(j => j.Date).ToListAsync();
            if (!entries.Any())
                return Results.NotFound("No hay asientos para el período seleccionado.");

            var balanceAccounts = studioCompanies
                .Where(c => !string.IsNullOrWhiteSpace(c.BankAccountName))
                .Select(c => c.BankAccountName!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            Guid.TryParse(currentTenant.StudioTenantId, out var studioGuidGet);
            var externalCodesGet = await dbContext.ChartOfAccounts
                .AsNoTracking()
                .Where(a => a.ExternalCode != null && (a.StudioTenantId == null || a.StudioTenantId == studioGuidGet))
                .ToDictionaryAsync(a => a.Name, a => a.ExternalCode!);

            var fileBytes = exportService.ExportJournalEntriesToExcel(entries, companyName, month, year, balanceAccounts, externalCodesGet);
            var dateLabel = month.HasValue && year.HasValue
                ? $"{month:D2}-{year}"
                : year.HasValue ? $"{year}" : "todo";
            var fileName  = $"LibroDiario_{companyName.Replace(" ", "_")}_{dateLabel}.xlsx";

            return Results.File(fileBytes,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                fileName);
        })
        .WithName("ExportJournalEntries")
        .WithTags("Libro Diario")
        .WithSummary("Exportar asientos del período a Excel (.xlsx).")
        .WithDescription("Genera el libro diario formateado en Excel. Query params: companyId (guid), month (int), year (int). Devuelve el archivo con Content-Disposition: attachment.")
        .Produces(200)
        .Produces(404);


        app.MapGet("/api/journal-entries/export/holistor", async (
            ICurrentTenantService currentTenant,
            ContableAIDbContext   dbContext,
            [FromServices] IExportService exportService,
            [FromQuery] string? companyId,
            [FromQuery] int?    month,
            [FromQuery] int?    year) =>
        {
            string companyName = "Empresa";
            if (!string.IsNullOrWhiteSpace(companyId) && Guid.TryParse(companyId, out var cGuid))
            {
                var co = await dbContext.Companies.FindAsync(cGuid);
                if (co != null) companyName = co.Name;
            }

            var studioCompanyIds = await dbContext.Companies
                .Where(c => c.StudioTenantId == currentTenant.StudioTenantId && c.IsActive)
                .Select(c => c.Id)
                .ToListAsync();

            var query = dbContext.JournalEntries
                .Include(j => j.Lines)
                .Where(j => j.CompanyId.HasValue && studioCompanyIds.Contains(j.CompanyId.Value));

            if (!string.IsNullOrWhiteSpace(companyId) && Guid.TryParse(companyId, out var expCmpId))
                query = query.Where(j => j.CompanyId == expCmpId);

            if (month.HasValue && year.HasValue)
            {
                var start = new DateOnly(year.Value, month.Value, 1);
                var end   = start.AddMonths(1).AddDays(-1);
                query = query.Where(j => j.Date >= start && j.Date <= end);
            }
            else if (year.HasValue)
            {
                var start = new DateOnly(year.Value, 1, 1);
                var end   = new DateOnly(year.Value, 12, 31);
                query = query.Where(j => j.Date >= start && j.Date <= end);
            }

            var entries = await query.OrderBy(j => j.Date).ToListAsync();
            if (!entries.Any())
                return Results.NotFound("No hay asientos para el período seleccionado.");

            Guid.TryParse(currentTenant.StudioTenantId, out var studioGuidHol);
            var externalCodesHol = await dbContext.ChartOfAccounts
                .AsNoTracking()
                .Where(a => a.ExternalCode != null && (a.StudioTenantId == null || a.StudioTenantId == studioGuidHol))
                .ToDictionaryAsync(a => a.Name, a => a.ExternalCode!);

            var fileBytes = exportService.ExportJournalEntriesToHolistor(entries, externalCodesHol);
            var dateLabel = month.HasValue && year.HasValue
                ? $"{month:D2}-{year}"
                : year.HasValue ? $"{year}" : "todo";
            var fileName  = $"Holistor_{companyName.Replace(" ", "_")}_{dateLabel}.txt";

            return Results.File(fileBytes, "text/plain; charset=utf-8", fileName);
        })
        .WithName("ExportJournalEntriesHolistor")
        .WithTags("Libro Diario")
        .WithSummary("Exportar asientos en formato Holistor (.txt).")
        .WithDescription("Genera texto plano compatible con el formato de importación de Holistor. Query params: companyId (guid), month (int), year (int).")
        .Produces(200)
        .Produces(404);


        app.MapGet("/api/journal-entries/export/bejerman", async (
            ICurrentTenantService currentTenant,
            ContableAIDbContext   dbContext,
            [FromServices] IExportService exportService,
            [FromQuery] string? companyId,
            [FromQuery] int?    month,
            [FromQuery] int?    year) =>
        {
            string companyName = "Empresa";
            if (!string.IsNullOrWhiteSpace(companyId) && Guid.TryParse(companyId, out var cGuid))
            {
                var co = await dbContext.Companies.FindAsync(cGuid);
                if (co != null) companyName = co.Name;
            }

            var studioCompanyIds = await dbContext.Companies
                .Where(c => c.StudioTenantId == currentTenant.StudioTenantId && c.IsActive)
                .Select(c => c.Id)
                .ToListAsync();

            var query = dbContext.JournalEntries
                .Include(j => j.Lines)
                .Where(j => j.CompanyId.HasValue && studioCompanyIds.Contains(j.CompanyId.Value));

            if (!string.IsNullOrWhiteSpace(companyId) && Guid.TryParse(companyId, out var expCmpId))
                query = query.Where(j => j.CompanyId == expCmpId);

            if (month.HasValue && year.HasValue)
            {
                var start = new DateOnly(year.Value, month.Value, 1);
                var end   = start.AddMonths(1).AddDays(-1);
                query = query.Where(j => j.Date >= start && j.Date <= end);
            }
            else if (year.HasValue)
            {
                var start = new DateOnly(year.Value, 1, 1);
                var end   = new DateOnly(year.Value, 12, 31);
                query = query.Where(j => j.Date >= start && j.Date <= end);
            }

            var entries = await query.OrderBy(j => j.Date).ToListAsync();
            if (!entries.Any())
                return Results.NotFound("No hay asientos para el período seleccionado.");

            Guid.TryParse(currentTenant.StudioTenantId, out var studioGuidBej);
            var externalCodesBej = await dbContext.ChartOfAccounts
                .AsNoTracking()
                .Where(a => a.ExternalCode != null && (a.StudioTenantId == null || a.StudioTenantId == studioGuidBej))
                .ToDictionaryAsync(a => a.Name, a => a.ExternalCode!);

            var fileBytes = exportService.ExportJournalEntriesToBejerman(entries, externalCodesBej);
            var dateLabel = month.HasValue && year.HasValue
                ? $"{month:D2}-{year}"
                : year.HasValue ? $"{year}" : "todo";
            var fileName  = $"Bejerman_{companyName.Replace(" ", "_")}_{dateLabel}.csv";

            return Results.File(fileBytes, "text/csv; charset=utf-8", fileName);
        })
        .WithName("ExportJournalEntriesBejerman")
        .WithTags("Libro Diario")
        .WithSummary("Exportar asientos en formato Bejerman (.csv).")
        .WithDescription("Genera un CSV compatible con el formato de importación de Bejerman. Query params: companyId (guid), month (int), year (int).")
        .Produces(200)
        .Produces(404);


        app.MapGet("/api/journal-entries/export/csv", async (
            ISender sender,
            [FromQuery] Guid    companyId,
            [FromQuery] int?    month,
            [FromQuery] int?    year) =>
        {
            var result = await sender.Send(new ExportJournalEntriesQuery(companyId, month, year));
            if (!result.IsSuccess)
                return result.StatusCode == 404
                    ? Results.NotFound(result.Error)
                    : Results.Problem(title: "Error", detail: result.Error, statusCode: result.StatusCode);

            var file = result.Value!;
            return Results.File(file.Content, file.ContentType, file.FileName);
        })
        .RequireAuthorization()
        .WithName("ExportJournalEntriesCsv")
        .WithTags("Libro Diario")
        .WithSummary("Exportar asientos a CSV estándar (Fecha, Asiento Nro, Concepto, Cuenta, Debe, Haber).")
        .WithDescription("Query params: companyId (guid, requerido), month (int), year (int). Genera un CSV con una fila por línea contable, listo para importar en cualquier software contable.")
        .Produces(200, contentType: "text/csv")
        .Produces(404);
    }

    private static string BuildEntrySignature(JournalEntry entry)
        => BuildEntrySignature(entry.CompanyId, entry.Date, entry.Description, entry.Lines);

    private static string BuildEntrySignature(Guid? companyId, DateOnly date, string description, IEnumerable<JournalEntryLine> lines)
    {
        var normalizedDescription = NormalizeDescription(description);
        var lineSignature = string.Join(";", lines
            .OrderBy(l => l.Account, StringComparer.OrdinalIgnoreCase)
            .ThenBy(l => l.IsDebit)
            .ThenBy(l => l.Amount)
            .Select(l => string.Join("|",
                l.Account.Trim().ToUpperInvariant(),
                l.IsDebit ? "D" : "H",
                l.Amount.ToString("0.00", CultureInfo.InvariantCulture))));

        return string.Join("#",
            companyId?.ToString() ?? "NO_COMPANY",
            date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            normalizedDescription,
            lineSignature);
    }

    private static string NormalizeDescription(string description)
        => string.Join(' ', (description ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToUpperInvariant();
}

public sealed class ExportJournalEntriesRequest
{
    public string? CompanyId { get; set; }
    public int? Month { get; set; }
    public int? Year { get; set; }
    public string? Search { get; set; }
    public string? Account { get; set; }
    public List<string>? EntryIds { get; set; }
}
