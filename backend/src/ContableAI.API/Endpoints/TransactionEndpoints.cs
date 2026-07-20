using ContableAI.API.Common;
using ContableAI.Application.Features.Transactions.Commands;
using ContableAI.Domain.Common;
using ContableAI.Domain.Constants;
using ContableAI.Domain.Entities;
using ContableAI.Domain.Enums;
using ContableAI.Infrastructure.Persistence;
using ContableAI.Infrastructure.Services;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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
            ISender sender,
            [FromForm] bool? withoutDateFilter = null,
            [FromForm] bool? forceReapplyRules = null) =>
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
            var result = await sender.Send(new UploadBankStatementCommand(fileDataList, cId, bankCode, withoutDateFilter ?? false, forceReapplyRules ?? false));
            return result.ToHttpResult();
        })
        .DisableAntiforgery()
        .WithName("UploadBankStatement")
        .WithTags("Transacciones")
        .WithSummary("Importar y clasificar extractos bancarios (CSV, XLSX, PDF).")
        .WithDescription("Form-data multipart: files[] (uno o más archivos), bankCode (AUTO | BBVA | GALICIA | ...), companyId (guid). Detecta duplicados en BD y dentro del lote. Auto-detecta banco desde PDF si bankCode = AUTO. Valida cuota mensual del plan.")
        .Produces(200)
        .Produces<ProblemDetails>(402);


        app.MapGet("/api/transactions", async (
            ICurrentTenantService  tenant,
            ContableAIDbContext    dbContext,
            [FromQuery] string?  companyId,
            [FromQuery] int?     month,
            [FromQuery] int?     year,
            [FromQuery] string?  search,
            [FromQuery] string?  account,
            [FromQuery] string?  sortBy,
            [FromQuery] string?  sortDir,
            [FromQuery] bool     strictSearch = false,
            [FromQuery] int?     type        = null,
            [FromQuery] decimal? exactAmount = null,
            [FromQuery] decimal? minAmount   = null,
            [FromQuery] decimal? maxAmount   = null,
            [FromQuery] string?  currency    = null,
            [FromQuery] int      page        = 1,
            [FromQuery] int      pageSize    = 100) =>
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
            {
                if (strictSearch)
                    query = query.Where(t => t.Description.Contains(search));
                else
                {
                    static string RemoveDiacritics(string text)
                    {
                        var normalized = text.Normalize(NormalizationForm.FormD);
                        return new string(normalized
                            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                            .ToArray());
                    }
                    var normalizedSearch = RemoveDiacritics(search.ToLower());
                    query = query.Where(t => EF.Functions.ILike(
                        ContableAIDbContext.Unaccent(t.Description), $"%{normalizedSearch}%"));
                }
            }

            if (!string.IsNullOrWhiteSpace(account))
            {
                query = string.Equals(account, "Pending", StringComparison.OrdinalIgnoreCase)
                    ? query.Where(t => t.AssignedAccount == null || t.AssignedAccount == string.Empty || t.AssignedAccount == "Pending")
                    // Case-insensitive: legacy data con casing distinto ("cargas sociales" vs
                    // "Cargas Sociales") debe aparecer igual bajo la cuenta canónica seleccionada.
                    : query.Where(t => t.AssignedAccount != null && t.AssignedAccount.ToLower() == account.ToLower());
            }

            if (type.HasValue)
                query = query.Where(t => (int)t.Type == type.Value);

            // Filtro por moneda (usa el índice IX_BankTransactions_CompanyId_Currency).
            if (!string.IsNullOrWhiteSpace(currency))
                query = query.Where(t => t.Currency == currency);

            if (exactAmount.HasValue)
                query = query.Where(t => t.Amount == exactAmount.Value);
            else
            {
                if (minAmount.HasValue)
                    query = query.Where(t => t.Amount >= minAmount.Value);
                if (maxAmount.HasValue)
                    query = query.Where(t => t.Amount <= maxAmount.Value);
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
            // GROUP BY (Type, Currency): permite totales por moneda sin sumar ARS con USD.
            var filteredStats = await query
                .GroupBy(t => new { t.Type, t.Currency })
                .Select(g => new { g.Key.Type, g.Key.Currency, Count = g.Count(), Total = g.Sum(t => t.Amount) })
                .ToListAsync();

            var totalCount            = filteredStats.Sum(x => x.Count);
            var totalIngresosFiltered = filteredStats.Where(x => x.Type == TransactionType.Credit).Sum(x => x.Total);
            var totalEgresosFiltered  = filteredStats.Where(x => x.Type == TransactionType.Debit).Sum(x => x.Total);

            // Totales por moneda: la UI muestra una línea por moneda, nunca ARS+USD fusionados.
            var currencyTotals = filteredStats
                .GroupBy(x => x.Currency)
                .Select(g => new
                {
                    Currency = g.Key,
                    Ingresos = g.Where(x => x.Type == TransactionType.Credit).Sum(x => x.Total),
                    Egresos  = g.Where(x => x.Type == TransactionType.Debit).Sum(x => x.Total),
                })
                .OrderBy(x => x.Currency == Currencies.Ars ? 0 : 1)
                .ThenBy(x => x.Currency)
                .ToList();

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
                CurrencyTotals        = currencyTotals,
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
            [FromServices] IAccountNameResolver accountResolver,
            ContableAIDbContext dbContext,
            ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger(nameof(TransactionEndpoints));
            // FirstOrDefaultAsync aplica el Global Query Filter: una transacción de otro
            // estudio devuelve null → NotFound. Cierra el IDOR de reasignación cross-tenant.
            var tx = await dbContext.BankTransactions.FirstOrDefaultAsync(t => t.Id == id);
            if (tx == null) return Results.NotFound();

            if (await PeriodEndpoints.IsPeriodClosedAsync(dbContext, currentTenant.StudioTenantId!, tx.Date.Year, tx.Date.Month))
                return Results.Problem(
                    title:      "Período cerrado",
                    detail:     $"El período {tx.Date.Month:D2}/{tx.Date.Year} está cerrado. Reabrilo antes de modificar esta transacción.",
                    statusCode: 422);

            Guid.TryParse(currentTenant.StudioTenantId, out var studioGuid);
            var canonicalAccount = await accountResolver.ResolveAsync(request.AssignedAccount, studioGuid);
            tx.Assign(canonicalAccount, null, false, ClassificationSources.Manual);
            await dbContext.SaveChangesAsync();
            var newSuggestionKeyword = await CheckManualSuggestionAsync(dbContext, tx.CompanyId, currentTenant.StudioTenantId!, tx.Description, tx.AssignedAccount, logger);
            return Results.Ok(new { Transaction = tx, NewSuggestionKeyword = newSuggestionKeyword });
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
            [FromServices] IAccountNameResolver accountResolver,
            ContableAIDbContext dbContext,
            ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger(nameof(TransactionEndpoints));
            if (request.Ids == null || request.Ids.Count == 0)
                return Results.BadRequest("Se requiere al menos un ID.");

            if (string.IsNullOrWhiteSpace(request.AssignedAccount))
                return Results.BadRequest("La cuenta contable es obligatoria.");

            // El Global Query Filter de tenant se aplica acá: los IDs que pertenezcan a otro
            // estudio quedan fuera del resultado, por lo que nunca se modifican (fix IDOR bulk).
            var transactions = await dbContext.BankTransactions
                .Where(t => request.Ids.Contains(t.Id))
                .ToListAsync();

            if (transactions.Count == 0)
                return Results.NotFound("No se encontraron transacciones con los IDs indicados.");

            AccountingRule? appliedRule = null;
            if (request.RuleId.HasValue)
            {
                appliedRule = await dbContext.AccountingRules
                    .AsNoTracking()
                    .FirstOrDefaultAsync(r => r.Id == request.RuleId.Value);

                if (appliedRule is null)
                    return Results.BadRequest("La regla seleccionada no existe.");

                var txCompanyIds = transactions
                    .Where(t => t.CompanyId.HasValue)
                    .Select(t => t.CompanyId!.Value)
                    .Distinct()
                    .ToList();

                if (txCompanyIds.Count > 1)
                    return Results.BadRequest("No se puede aplicar una regla a transacciones de distintas empresas.");

                if (appliedRule.CompanyId.HasValue && (txCompanyIds.Count == 0 || appliedRule.CompanyId.Value != txCompanyIds[0]))
                    return Results.BadRequest("La regla seleccionada pertenece a otra empresa.");
            }

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

            Guid.TryParse(currentTenant.StudioTenantId, out var studioGuid);
            var canonicalAccount = appliedRule is null
                ? (await accountResolver.BuildMapAsync(studioGuid)).Resolve(request.AssignedAccount)
                : request.AssignedAccount;

            foreach (var tx in transactions)
            {
                if (appliedRule is not null)
                {
                    tx.Assign(
                        appliedRule.TargetAccount,
                        appliedRule.Id,
                        appliedRule.RequiresTaxMatching,
                        ClassificationSources.HardRule,
                        appliedRule.RequiresTaxMatching ? 0.75f : 1.0f);
                }
                else
                {
                    tx.Assign(canonicalAccount, null, false, ClassificationSources.Manual);
                }
            }

            await dbContext.SaveChangesAsync();

            var newSuggestionKeywords = new List<string>();
            if (appliedRule is null)
            {
                var groups = transactions
                    .GroupBy(t => new { t.Description, t.AssignedAccount, t.CompanyId, t.TenantId })
                    .Select(g => g.First());
                foreach (var rep in groups)
                {
                    var kw = await CheckManualSuggestionAsync(dbContext, rep.CompanyId, currentTenant.StudioTenantId!, rep.Description, rep.AssignedAccount, logger);
                    if (kw is not null) newSuggestionKeywords.Add(kw);
                }
            }

            return Results.Ok(new
            {
                UpdatedCount          = transactions.Count,
                AssignedAccount       = appliedRule?.TargetAccount ?? request.AssignedAccount,
                AppliedRuleId         = appliedRule?.Id,
                ClassificationSource  = appliedRule is null ? ClassificationSources.Manual : ClassificationSources.HardRule,
                Transactions          = transactions,
                NewSuggestionKeywords = newSuggestionKeywords,
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
            var studioCompanyIds = await dbContext.Companies
                .AsNoTracking()
                .Where(c => c.StudioTenantId == tenant.StudioTenantId && c.IsActive)
                .Select(c => c.Id)
                .ToListAsync();

            string companyName = "Empresa";
            if (!string.IsNullOrWhiteSpace(companyId) && Guid.TryParse(companyId, out var cIdGuid))
            {
                var company = await dbContext.Companies
                    .AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Id == cIdGuid);
                if (company != null) companyName = company.Name;
            }

            var query = dbContext.BankTransactions
                .AsNoTracking()
                .Where(t => t.CompanyId.HasValue && studioCompanyIds.Contains(t.CompanyId.Value))
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(companyId) && Guid.TryParse(companyId, out var expCmpId))
                query = query.Where(t => t.CompanyId == expCmpId);

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

            var transactions = await query.OrderBy(t => t.Date).ToListAsync();

            if (!transactions.Any())
            {
                var periodMsg = month.HasValue && year.HasValue ? $"{month:D2}/{year}"
                              : year.HasValue  ? $"{year}"
                              : month.HasValue ? $"mes {month}"
                              : "el período seleccionado";
                return Results.NotFound($"No hay transacciones para {periodMsg}.");
            }

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
            var dateLabel = month.HasValue && year.HasValue ? $"{month:D2}-{year}"
                          : year.HasValue  ? $"{year}"
                          : month.HasValue ? $"mes{month:D2}"
                          : "todo";
            var fileName  = $"Banco_{companyName.Replace(" ", "_")}_{dateLabel}.csv";

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

    private static async Task<string?> CheckManualSuggestionAsync(
        ContableAIDbContext db,
        Guid? companyId,
        string tenantId,
        string description,
        string? assignedAccount,
        ILogger logger,
        CancellationToken ct = default)
    {
        if (companyId is null || string.IsNullOrWhiteSpace(assignedAccount)) return null;

        var keyword = KeywordNormalizer.Normalize(description);

        logger.LogDebug("Suggestion check: Start — Company={CompanyId}, Keyword={Keyword}, Account={Account}", companyId, keyword, assignedAccount);

        if (string.IsNullOrWhiteSpace(keyword)) return null;

        bool ruleExists = await db.AccountingRules.AnyAsync(
            r => r.CompanyId == companyId && r.Keyword == keyword, ct);

        if (ruleExists)
        {
            logger.LogDebug("Suggestion check: Skip — Keyword={Keyword}, Reason=RuleAlreadyExists", keyword);
            return null;
        }

        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-730));

        var descriptions = await db.BankTransactions
            .Where(t => t.CompanyId == companyId
                     && t.AssignedAccount == assignedAccount
                     && t.ClassificationSource == ClassificationSources.Manual
                     && t.Date >= cutoff)
            .Select(t => t.Description)
            .ToListAsync(ct);

        var matchedDescriptions = descriptions
            .Select(d => new { Original = d, Normalized = KeywordNormalizer.Normalize(d) })
            .Where(x => x.Normalized == keyword)
            .ToList();

        var count = matchedDescriptions.Count;

        logger.LogDebug("Suggestion check: Count — Keyword={Keyword}, MatchCount={MatchCount}, ThresholdMet={ThresholdMet}", keyword, count, count >= 3);

        if (count < 3) return null;

        var existing = await db.RuleSuggestions
            .FirstOrDefaultAsync(s => s.CompanyId == companyId && s.Keyword == keyword, ct);

        if (existing != null)
        {
            if (existing.Status == SuggestionStatus.Rejected)
            {
                existing.Status           = SuggestionStatus.Pending;
                existing.SuggestedAccount = assignedAccount;
                existing.Frequency        = count;
                await db.SaveChangesAsync(ct);
                logger.LogDebug("Suggestion check: Reactivated — Keyword={Keyword}, Account={Account}, Frequency={Frequency}", keyword, assignedAccount, count);
                return keyword;
            }
            else if (existing.Status == SuggestionStatus.Pending && existing.Frequency < count)
            {
                existing.Frequency = count;
                await db.SaveChangesAsync(ct);
                logger.LogDebug("Suggestion check: Updated — Keyword={Keyword}, NewFrequency={NewFrequency}", keyword, count);
            }
            return null;
        }

        db.RuleSuggestions.Add(new RuleSuggestion
        {
            TenantId         = tenantId,
            CompanyId        = companyId,
            Keyword          = keyword,
            SuggestedAccount = assignedAccount,
            Frequency        = count,
            Status           = SuggestionStatus.Pending
        });
        await db.SaveChangesAsync(ct);
        logger.LogDebug("Suggestion check: Created — Keyword={Keyword}, Account={Account}, Frequency={Frequency}", keyword, assignedAccount, count);
        return keyword;
    }
}
