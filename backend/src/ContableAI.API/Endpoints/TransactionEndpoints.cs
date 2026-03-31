using ContableAI.API.Common;
using ContableAI.Application.Features.Transactions.Commands;
using ContableAI.Domain.Constants;
using ContableAI.Domain.Entities;
using ContableAI.Domain.Enums;
using ContableAI.Infrastructure.Persistence;
using ContableAI.Infrastructure.Services;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Text;

namespace ContableAI.API.Endpoints;

public static class TransactionEndpoints
{
    public static void MapTransactionEndpoints(this WebApplication app)
    {

        app.MapPost("/api/transactions/upload", async (
            HttpContext httpCtx,
            [FromForm] string? bankCode,
            [FromForm] string? companyId,
            ISender sender) =>
        {
            var files = httpCtx.Request.Form.Files;
            if (files is null || files.Count == 0)
                return Results.BadRequest("No se subió ningún archivo.");

            var fileDataList = new List<FileData>();
            foreach (var f in files)
            {
                using var ms = new MemoryStream((int)Math.Max(0, f.Length));
                await f.CopyToAsync(ms);
                fileDataList.Add(new FileData(ms.ToArray(), f.FileName ?? "", f.Length));
            }

            Guid? cId = Guid.TryParse(companyId, out var g) ? g : null;
            var result = await sender.Send(new UploadBankStatementCommand(fileDataList, cId, bankCode));
            return result.ToHttpResult();
        })
        .DisableAntiforgery()
        .WithName("UploadBankStatement")
        .WithTags("Transacciones")
        .WithSummary("Importar y clasificar extractos bancarios (CSV, XLSX, PDF).")
        .WithDescription("Form-data multipart: files[] (uno o más archivos), bankCode (AUTO | BBVA | GALICIA | ...), companyId (guid). Detecta duplicados en BD y dentro del lote. Auto-detecta banco desde PDF si bankCode = AUTO. Valida cuota mensual del plan.")
        .Produces(200)
        .Produces<ProblemDetails>(402);


        // ── Endpoint de descarga del log de parseo acumulativo ────────────────
        app.MapGet("/api/transactions/parse-log", () =>
        {
            var logPath = Path.Combine(Directory.GetCurrentDirectory(), "logs", "parse-log.jsonl");
            if (!File.Exists(logPath))
                return Results.NotFound(new { Message = "El log de parseo todavía no tiene entradas." });
            return Results.File(logPath, "application/x-ndjson", "parse-log.jsonl");
        })
        .WithName("GetParseLog")
        .WithTags("Transacciones")
        .WithSummary("Descarga el log acumulativo JSONL de todos los extractos parseados.")
        .RequireAuthorization(p => p.RequireRole(UserRole.StudioOwner.ToString(), UserRole.SystemAdmin.ToString()))
        .Produces(200)
        .Produces(404);


        app.MapGet("/api/transactions", async (
            ICurrentTenantService  tenant,
            ContableAIDbContext    dbContext,
            [FromQuery] string? companyId,
            [FromQuery] int?    month,
            [FromQuery] int?    year,
            [FromQuery] string? search,
            [FromQuery] string? account,
            [FromQuery] string? sortBy,
            [FromQuery] string? sortDir,
            [FromQuery] int     page     = 1,
            [FromQuery] int     pageSize = 100) =>
        {
            var studioId = tenant.StudioTenantId;

            var studioCompanyIds = await dbContext.Companies
                .AsNoTracking()
                .Where(c => c.StudioTenantId == studioId && c.IsActive)
                .Select(c => c.Id)
                .ToListAsync();

            var query = dbContext.BankTransactions
                .AsNoTracking()
                .Where(t => t.CompanyId.HasValue && studioCompanyIds.Contains(t.CompanyId.Value));

            if (!string.IsNullOrWhiteSpace(companyId) && Guid.TryParse(companyId, out var txCompanyId))
                query = query.Where(t => t.CompanyId == txCompanyId);

            if (month.HasValue && year.HasValue)
            {
                var startDate = new DateOnly(year.Value, month.Value, 1);
                var endDate   = startDate.AddMonths(1).AddDays(-1);
                query = query.Where(t => t.Date >= startDate && t.Date <= endDate);
            }
            else if (year.HasValue)
            {
                var startDate = new DateOnly(year.Value, 1, 1);
                var endDate   = new DateOnly(year.Value, 12, 31);
                query = query.Where(t => t.Date >= startDate && t.Date <= endDate);
            }
            else if (month.HasValue)
            {
                query = query.Where(t => t.Date.Month == month.Value);
            }

            if (!string.IsNullOrWhiteSpace(search))
                query = query.Where(t => t.Description.Contains(search));

            if (!string.IsNullOrWhiteSpace(account))
            {
                query = string.Equals(account, "Pending", StringComparison.OrdinalIgnoreCase)
                    ? query.Where(t => t.AssignedAccount == null || t.AssignedAccount == string.Empty || t.AssignedAccount == "Pending")
                    : query.Where(t => t.AssignedAccount == account);
            }

            var filterBaseQuery = dbContext.BankTransactions
                .AsNoTracking()
                .Where(t => t.CompanyId.HasValue && studioCompanyIds.Contains(t.CompanyId.Value));

            if (!string.IsNullOrWhiteSpace(companyId) && Guid.TryParse(companyId, out var filterCompanyId))
                filterBaseQuery = filterBaseQuery.Where(t => t.CompanyId == filterCompanyId);

            if (!string.IsNullOrWhiteSpace(search))
                filterBaseQuery = filterBaseQuery.Where(t => t.Description.Contains(search));

            // ── Consolidación 1: 3 queries → 1 (accounts + months + years) ──
            // Una sola query proyecta las 3 columnas livianas; los Distinct/Sort se hacen en memoria.
            var filterMeta = await filterBaseQuery
                .Select(t => new { t.AssignedAccount, Month = t.Date.Month, Year = t.Date.Year })
                .ToListAsync();

            var normalizedAccounts = filterMeta
                .Select(x => string.IsNullOrWhiteSpace(x.AssignedAccount) ? "Pending" : x.AssignedAccount.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(a => a == "Pending" ? 0 : 1)
                .ThenBy(a => a, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var availableMonths = filterMeta.Select(x => x.Month).Distinct().OrderBy(m => m).ToList();
            var availableYears  = filterMeta.Select(x => x.Year).Distinct().OrderByDescending(y => y).ToList();

            pageSize = Math.Clamp(pageSize, 1, 500);
            page     = Math.Max(1, page);

            // ── Consolidación 2: 3 queries → 1 (count + ingresos + egresos filtrados) ──
            // GROUP BY Type genera: SELECT Type, COUNT(*), SUM(Amount) en una sola roundtrip.
            var filteredStats = await query
                .GroupBy(t => t.Type)
                .Select(g => new { Type = g.Key, Count = g.Count(), Total = g.Sum(t => t.Amount) })
                .ToListAsync();

            var totalCount            = filteredStats.Sum(x => x.Count);
            var totalIngresosFiltered = filteredStats.FirstOrDefault(x => x.Type == TransactionType.Credit)?.Total ?? 0m;
            var totalEgresosFiltered  = filteredStats.FirstOrDefault(x => x.Type == TransactionType.Debit)?.Total ?? 0m;

            // ── Consolidación 3: 2 queries → 1 (ingresos + egresos totales sin filtro fecha/cuenta) ──
            var queryAll = dbContext.BankTransactions
                .AsNoTracking()
                .Where(t => t.CompanyId.HasValue && studioCompanyIds.Contains(t.CompanyId.Value));
            if (!string.IsNullOrWhiteSpace(companyId) && Guid.TryParse(companyId, out var cIdAll))
                queryAll = queryAll.Where(t => t.CompanyId == cIdAll);

            var allStats = await queryAll
                .GroupBy(t => t.Type)
                .Select(g => new { Type = g.Key, Total = g.Sum(t => t.Amount) })
                .ToListAsync();

            var totalIngresosAll = allStats.FirstOrDefault(x => x.Type == TransactionType.Credit)?.Total ?? 0m;
            var totalEgresosAll  = allStats.FirstOrDefault(x => x.Type == TransactionType.Debit)?.Total ?? 0m;

            bool asc = string.Equals(sortDir, "asc", StringComparison.OrdinalIgnoreCase);
            IOrderedQueryable<BankTransaction> orderedQuery = sortBy?.ToLowerInvariant() switch
            {
                "description" => asc ? query.OrderBy(t => t.Description)    : query.OrderByDescending(t => t.Description),
                "amount"      => asc ? query.OrderBy(t => t.Amount)         : query.OrderByDescending(t => t.Amount),
                "account"     => asc ? query.OrderBy(t => t.AssignedAccount): query.OrderByDescending(t => t.AssignedAccount),
                "source"      => asc ? query.OrderBy(t => t.ClassificationSource) : query.OrderByDescending(t => t.ClassificationSource),
                "date"        => asc ? query.OrderBy(t => t.Date).ThenBy(t => t.SortOrder)
                                     : query.OrderByDescending(t => t.Date).ThenByDescending(t => t.SortOrder),
                _             => query.OrderBy(t => t.SortOrder).ThenBy(t => t.Date),
            };

            var items = await orderedQuery
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            return Results.Ok(new
            {
                Items                 = items,
                TotalCount            = totalCount,
                Page                  = page,
                PageSize              = pageSize,
                TotalPages            = (int)Math.Ceiling(totalCount / (double)pageSize),
                TotalIngresosFiltered = totalIngresosFiltered,
                TotalEgresosFiltered  = totalEgresosFiltered,
                TotalIngresosAll      = totalIngresosAll,
                TotalEgresosAll       = totalEgresosAll,
                AvailableAccounts     = normalizedAccounts,
                AvailableMonths       = availableMonths,
                AvailableYears        = availableYears,
            });
        })
        .WithName("GetTransactions")
        .WithTags("Transacciones")
        .WithSummary("Listar transacciones paginadas del estudio, filtrables por empresa, período y búsqueda.")
        .WithDescription("Query params: companyId (guid), month (int), year (int), search (string), page (defecto 1), pageSize (defecto 100, máx 500). Ordenadas por fecha descendente.")
        .Produces(200);


        app.MapPut("/api/transactions/{id:guid}", async (
            Guid id,
            UpdateAccountRequest request,
            [FromServices] ICurrentTenantService currentTenant,
            ContableAIDbContext dbContext) =>
        {
            var tx = await dbContext.BankTransactions.FindAsync(id);
            if (tx == null) return Results.NotFound();

            if (await PeriodEndpoints.IsPeriodClosedAsync(dbContext, currentTenant.StudioTenantId!, tx.Date.Year, tx.Date.Month))
                return Results.Problem(
                    title:      "Período cerrado",
                    detail:     $"El período {tx.Date.Month:D2}/{tx.Date.Year} está cerrado. Reabrilo antes de modificar esta transacción.",
                    statusCode: 422);

            tx.Assign(request.AssignedAccount, null, false, ClassificationSources.Manual);
            await dbContext.SaveChangesAsync();
            return Results.Ok(tx);
        })
        .WithName("UpdateTransaction")
        .WithTags("Transacciones")
        .WithSummary("Asignar manualmente una cuenta contable a una transacción.")
        .WithDescription("Body: { assignedAccount: string }. No permite modificar transacciones de períodos cerrados.")
        .Produces(200)
        .Produces<ProblemDetails>(422);


        app.MapPut("/api/transactions/bulk", async (
            BulkUpdateRequest request,
            [FromServices] ICurrentTenantService currentTenant,
            ContableAIDbContext dbContext) =>
        {
            if (request.Ids == null || request.Ids.Count == 0)
                return Results.BadRequest("Se requiere al menos un ID.");

            if (string.IsNullOrWhiteSpace(request.AssignedAccount))
                return Results.BadRequest("La cuenta contable es obligatoria.");

            var transactions = await dbContext.BankTransactions
                .Where(t => request.Ids.Contains(t.Id))
                .ToListAsync();

            if (transactions.Count == 0)
                return Results.NotFound("No se encontraron transacciones con los IDs indicados.");

            var periodsToValidate = transactions
                .Select(t => new { t.Date.Year, t.Date.Month })
                .Distinct()
                .ToList();

            foreach (var period in periodsToValidate)
            {
                if (await PeriodEndpoints.IsPeriodClosedAsync(dbContext, currentTenant.StudioTenantId!, period.Year, period.Month))
                {
                    var tx = transactions.First(t => t.Date.Year == period.Year && t.Date.Month == period.Month);
                    return Results.Problem(
                        title:      "Período cerrado",
                        detail:     $"La transacción del {tx.Date:dd/MM/yyyy} pertenece al período {tx.Date.Month:D2}/{tx.Date.Year} que está cerrado.",
                        statusCode: 422);
                }
            }

            foreach (var tx in transactions)
                tx.Assign(request.AssignedAccount, null, false, ClassificationSources.Manual);

            await dbContext.SaveChangesAsync();

            return Results.Ok(new
            {
                UpdatedCount     = transactions.Count,
                AssignedAccount  = request.AssignedAccount,
                Transactions     = transactions,
            });
        })
        .WithName("BulkUpdateTransactions")
        .WithTags("Transacciones")
        .WithSummary("Asignar cuenta contable a múltiples transacciones en lote.")
        .WithDescription("Body: { ids: [guid], assignedAccount: string }. Valida períodos cerrados para TODAS las transacciones antes de confirmar cualquier cambio. Transacción atómica.")
        .Produces(200)
        .Produces<ProblemDetails>(422);


        app.MapDelete("/api/transactions", async (
            ICurrentTenantService tenant,
            ContableAIDbContext dbContext) =>
        {
            var studioId = tenant.StudioTenantId;

            var studioCompanyIds = await dbContext.Companies
                .AsNoTracking()
                .Where(c => c.StudioTenantId == studioId && c.IsActive)
                .Select(c => c.Id)
                .ToListAsync();

            var allTransactions = await dbContext.BankTransactions
                .Where(t => t.CompanyId.HasValue && studioCompanyIds.Contains(t.CompanyId.Value))
                .ToListAsync();

            if (allTransactions.Count == 0)
                return Results.Ok(new { message = "No había movimientos para limpiar." });

            dbContext.BankTransactions.RemoveRange(allTransactions);
            await dbContext.SaveChangesAsync();
            return Results.Ok(new { message = $"Se eliminaron {allTransactions.Count} movimientos." });
        })
        .WithName("DeleteAllTransactions")
        .WithTags("Transacciones")
        .WithSummary("Eliminar TODAS las transacciones (utilidad de reset/dev). Usar con extrema precaución.")
        .RequireAuthorization(p => p.RequireRole(UserRole.StudioOwner.ToString(), UserRole.SystemAdmin.ToString()))
        .Produces(200);


        app.MapGet("/api/transactions/unbooked-ids", async (
            ICurrentTenantService  tenant,
            ContableAIDbContext    dbContext,
            [FromQuery] string?    companyId) =>
        {
            var studioId = tenant.StudioTenantId;

            var studioCompanyIds = await dbContext.Companies
                .AsNoTracking()
                .Where(c => c.StudioTenantId == studioId && c.IsActive)
                .Select(c => c.Id)
                .ToListAsync();

            var query = dbContext.BankTransactions
                .AsNoTracking()
                .Where(t => t.CompanyId.HasValue
                         && studioCompanyIds.Contains(t.CompanyId.Value)
                         && t.AssignedAccount != null
                         && t.JournalEntryId == null);

            if (!string.IsNullOrWhiteSpace(companyId) && Guid.TryParse(companyId, out var cGuid))
                query = query.Where(t => t.CompanyId == cGuid);

            var ids = await query.Select(t => t.Id).ToListAsync();
            return Results.Ok(ids);
        })
        .WithName("GetUnbookedTransactionIds")
        .WithTags("Transacciones")
        .WithSummary("IDs de transacciones clasificadas pero aún no asentadas en el libro diario.")
        .WithDescription("Query params: companyId (guid), month, year. Útil para el flujo de generación de asientos: primero obtener los IDs, luego enviarlos a POST /api/journal-entries/generate.")
        .Produces(200);


        app.MapGet("/api/transactions/export", async (
            [FromQuery] string? companyId,
            [FromQuery] int? month,
            [FromQuery] int? year,
            ICurrentTenantService tenant,
            ContableAIDbContext dbContext) =>
        {
            var refMonth = month ?? DateTime.Now.Month;
            var refYear  = year  ?? DateTime.Now.Year;

            var studioCompanyIds = await dbContext.Companies
                .AsNoTracking()
                .Where(c => c.StudioTenantId == tenant.StudioTenantId && c.IsActive)
                .Select(c => c.Id)
                .ToListAsync();

            string companyName = "Empresa";
            if (!string.IsNullOrWhiteSpace(companyId) && Guid.TryParse(companyId, out var cIdGuid))
            {
                var company = await dbContext.Companies.FindAsync(cIdGuid);
                if (company != null) companyName = company.Name;
            }

            var query = dbContext.BankTransactions
                .AsNoTracking()
                .Where(t => t.CompanyId.HasValue && studioCompanyIds.Contains(t.CompanyId.Value))
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(companyId) && Guid.TryParse(companyId, out var expCmpId))
                query = query.Where(t => t.CompanyId == expCmpId);

            var startDate = new DateOnly(refYear, refMonth, 1);
            var endDate   = startDate.AddMonths(1).AddDays(-1);
            query = query.Where(t => t.Date >= startDate && t.Date <= endDate);

            var transactions = await query.OrderBy(t => t.Date).ToListAsync();

            if (!transactions.Any())
                return Results.NotFound($"No hay transacciones para {refMonth}/{refYear}.");

            var csv = new StringBuilder();
            csv.AppendLine("Fecha,Descripcion,Importe,Tipo,CuentaAsignada,FuenteClasificacion,Confianza,IdExterno,Asentado");

            foreach (var tx in transactions)
            {
                static string Esc(string? value)
                {
                    if (string.IsNullOrEmpty(value)) return "\"\"";
                    return $"\"{value.Replace("\"", "\"\"")}\"";
                }

                var tipo = tx.Type == TransactionType.Debit ? "Debito" : "Credito";
                var asentado = tx.JournalEntryId.HasValue ? "SI" : "NO";
                var amount = tx.Amount.ToString("0.00", CultureInfo.InvariantCulture);
                var confidence = tx.ConfidenceScore.ToString("0.00", CultureInfo.InvariantCulture);

                csv.AppendLine(string.Join(",",
                    tx.Date.ToString("yyyy-MM-dd"),
                    Esc(tx.Description),
                    amount,
                    tipo,
                    Esc(tx.AssignedAccount),
                    Esc(tx.ClassificationSource),
                    confidence,
                    Esc(tx.ExternalId),
                    asentado));
            }

            var fileBytes = Encoding.UTF8.GetBytes(csv.ToString());
            var fileName  = $"Banco_{companyName.Replace(" ", "_")}_{refMonth:D2}-{refYear}.csv";

            return Results.File(fileBytes,
                "text/csv; charset=utf-8",
                fileName);
        })
        .WithName("ExportTransactions")
        .WithTags("Transacciones")
        .WithSummary("Exportar transacciones de una empresa y período a CSV (.csv).")
        .WithDescription("Query params: companyId (guid), month (int), year (int). Descarga un CSV con todas las transacciones del período incluyendo clasificación y confianza de IA.")
        .Produces(200, contentType: "text/csv")
        .Produces<ProblemDetails>(404);
    }
}
