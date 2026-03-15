using ContableAI.API.Common;
using ContableAI.Application.Features.JournalEntries.Commands;
using ContableAI.Domain.Constants;
using ContableAI.Domain.Entities;
using ContableAI.Domain.Enums;
using ContableAI.Infrastructure.Persistence;
using ContableAI.Infrastructure.Services;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Globalization;

namespace ContableAI.API.Endpoints;

public static class JournalEntriesEndpoints
{
    public static void MapJournalEntriesEndpoints(this WebApplication app)
    {

        app.MapPost("/api/journal-entries/generate", async (
            GenerateJournalEntriesRequest req,
            ICurrentTenantService          currentTenant,
            ContableAIDbContext            dbContext) =>
        {
            if (req.TransactionIds == null || req.TransactionIds.Count == 0)
                return Results.BadRequest("Se requiere al menos una transacción.");

            var studioCompanyIds = await dbContext.Companies
                .Where(c => c.StudioTenantId == currentTenant.StudioTenantId && c.IsActive)
                .Select(c => c.Id)
                .ToListAsync();

            var transactions = await dbContext.BankTransactions
                .Where(t => req.TransactionIds.Contains(t.Id)
                         && t.CompanyId.HasValue
                         && studioCompanyIds.Contains(t.CompanyId.Value)
                         && t.AssignedAccount != null
                         && t.JournalEntryId == null)
                .ToListAsync();

            if (transactions.Count == 0)
                return Results.Ok(new { Generated = 0, Message = "No hay transacciones elegibles (todas ya asentadas o sin cuenta asignada)." });

            foreach (var tx in transactions)
            {
                if (await PeriodEndpoints.IsPeriodClosedAsync(dbContext, currentTenant.StudioTenantId!, tx.Date.Year, tx.Date.Month))
                    return Results.Problem(
                        title:      "Período cerrado",
                        detail:     $"La transacción del {tx.Date:dd/MM/yyyy} pertenece al período {tx.Date.Month:D2}/{tx.Date.Year} que está cerrado. Reabrilo antes de generar asientos.",
                        statusCode: 422);
            }

            // Agrupar por empresa para resolver la cuenta bancaria de cada una
            var companyIds = transactions.Select(t => t.CompanyId!.Value).Distinct().ToList();
            var companiesMap = await dbContext.Companies
                .Where(c => companyIds.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, c => c.BankAccountName);

            // Verificar que todas las empresas tengan cuenta bancaria configurada
            var missingBank = companyIds
                .Where(id => !companiesMap.ContainsKey(id) || string.IsNullOrWhiteSpace(companiesMap[id]))
                .ToList();
            if (missingBank.Count > 0)
            {
                var names = await dbContext.Companies
                    .Where(c => missingBank.Contains(c.Id))
                    .Select(c => c.Name)
                    .ToListAsync();
                return Results.Problem(
                    title:      "Cuenta bancaria no configurada",
                    detail:     $"Las siguientes empresas no tienen cuenta bancaria configurada en su perfil: {string.Join(", ", names)}. Editá la empresa y completá el campo \"Cuenta bancaria\".",
                    statusCode: 422);
            }

            var entries = new List<JournalEntry>();
            var minTxDate = transactions.Min(t => t.Date);
            var maxTxDate = transactions.Max(t => t.Date);

            var existingEntries = await dbContext.JournalEntries
                .AsNoTracking()
                .Include(j => j.Lines)
                .Where(j => j.CompanyId.HasValue
                         && companyIds.Contains(j.CompanyId.Value)
                         && j.Date >= minTxDate
                         && j.Date <= maxTxDate)
                .ToListAsync();

            var signatureToEntryIds = existingEntries
                .GroupBy(BuildEntrySignature)
                .ToDictionary(
                    g => g.Key,
                    g => new Queue<Guid>(g.Select(e => e.Id)),
                    StringComparer.Ordinal);

            var duplicatesSkipped = 0;
            var linkedDuplicates = new List<object>();

            foreach (var tx in transactions)
            {
                JournalEntry entry;
                var bankAccount = companiesMap[tx.CompanyId!.Value];
                List<JournalEntryLine> projectedLines;

                if (tx.ClassificationSource == ClassificationSources.ChequeTaxSplit)
                {
                    // Impuesto al Cheque: split 50% Débitos / 50% Créditos Bancarios
                    var half1 = Math.Round(tx.Amount / 2, 2);
                    var half2 = tx.Amount - half1; // garantiza que la suma sea exacta

                    projectedLines =
                    [
                        new JournalEntryLine { Account = "Impuesto a los Débitos Bancarios",  Amount = half1, IsDebit = true  },
                        new JournalEntryLine { Account = "Impuesto a los Créditos Bancarios", Amount = half2, IsDebit = true  },
                        new JournalEntryLine { Account = bankAccount,                          Amount = tx.Amount, IsDebit = false },
                    ];

                    entry = new JournalEntry
                    {
                        Date              = tx.Date,
                        Description       = tx.Description,
                        CompanyId         = tx.CompanyId,
                        BankTransactionId = tx.Id,
                        Lines             = projectedLines,
                    };
                }
                else
                {
                    // Partida doble estándar:
                    // DÉBITO (dinero sale del banco):  Dr AssignedAccount / Cr CuentaBancaria
                    // CRÉDITO (dinero entra al banco): Dr CuentaBancaria  / Cr AssignedAccount
                    bool isDebit = tx.Type == TransactionType.Debit;

                    projectedLines =
                    [
                        new JournalEntryLine { Account = isDebit ? tx.AssignedAccount! : bankAccount, Amount = tx.Amount, IsDebit = true  },
                        new JournalEntryLine { Account = isDebit ? bankAccount : tx.AssignedAccount!, Amount = tx.Amount, IsDebit = false },
                    ];

                    entry = new JournalEntry
                    {
                        Date              = tx.Date,
                        Description       = tx.Description,
                        CompanyId         = tx.CompanyId,
                        BankTransactionId = tx.Id,
                        Lines             = projectedLines,
                    };
                }

                var signature = BuildEntrySignature(tx.CompanyId, tx.Date, tx.Description, projectedLines);
                if (signatureToEntryIds.TryGetValue(signature, out var existingEntryIds)
                    && existingEntryIds.Count > 0)
                {
                    // Solo se matchea contra asientos PRE-EXISTENTES (anteriores a este lote).
                    // Cada transacción consume un único asiento equivalente preexistente.
                    var existingEntryId = existingEntryIds.Dequeue();
                    if (existingEntryIds.Count == 0)
                        signatureToEntryIds.Remove(signature);

                    tx.MarkPossibleDuplicate();
                    tx.JournalEntryId = existingEntryId;
                    linkedDuplicates.Add(new
                    {
                        TransactionId  = tx.Id,
                        JournalEntryId = existingEntryId,
                    });
                    duplicatesSkipped++;
                    continue;
                }

                // NO agregamos la firma de este lote al mapa: así dos movimientos idénticos en el mismo
                // extracto se asientan normalmente en lugar de que el segundo sea falso "equivalente".
                entries.Add(entry);
                tx.JournalEntryId = entry.Id; // Marcar como asentada
            }

            if (entries.Count == 0)
            {
                await dbContext.SaveChangesAsync();
                return Results.Ok(new
                {
                    Generated = 0,
                    DuplicatesSkipped = duplicatesSkipped,
                    LinkedTransactions = linkedDuplicates,
                    Message = duplicatesSkipped > 0
                        ? "No se generaron asientos nuevos porque ya existían asientos equivalentes. Los movimientos quedaron marcados como asentados."
                        : "No hay transacciones elegibles (todas ya asentadas o sin cuenta asignada).",
                });
            }

            dbContext.JournalEntries.AddRange(entries);
            await dbContext.SaveChangesAsync();

            return Results.Ok(new
            {
                Generated    = entries.Count,
                DuplicatesSkipped = duplicatesSkipped,
                LinkedTransactions = linkedDuplicates,
                BankAccount  = string.Join(", ", companiesMap.Values.Distinct()),
                Entries      = entries.Select(e => new
                {
                    e.Id,
                    e.Date,
                    e.Description,
                    e.CompanyId,
                    e.BankTransactionId,
                    Lines = e.Lines.Select(l => new { l.Account, l.Amount, l.IsDebit }),
                }),
            });
        })
        .WithName("GenerateJournalEntries")
        .WithTags("Libro Diario")
        .WithSummary("Generar asientos contables desde transacciones clasificadas.")
        .WithDescription("Body: { transactionIds: [guid] }. Genera partida doble estándar o triple para Impuesto al Cheque (split 50/50). Valida períodos cerrados y que cada empresa tenga cuenta bancaria configurada. Omite transacciones ya asentadas.")
        .Produces(200)
        .Produces(400)
        .Produces(422);


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
                .Include(j => j.Lines)
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
                    Lines = j.Lines.OrderByDescending(l => l.IsDebit).Select(l => new { l.Account, l.Amount, l.IsDebit }),
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

            var fileBytes = exportService.ExportJournalEntriesToExcel(entries, companyName, req.Month, req.Year, balanceAccounts);
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

            var fileBytes = exportService.ExportJournalEntriesToExcel(entries, companyName, month, year, balanceAccounts);
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

            var fileBytes = exportService.ExportJournalEntriesToHolistor(entries);
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

            var fileBytes = exportService.ExportJournalEntriesToBejerman(entries);
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
