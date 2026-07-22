using ContableAI.API.Common;
using ContableAI.Application.Features.Transactions.Commands;
using ContableAI.Domain.Common;
using ContableAI.Domain.Constants;
using ContableAI.Domain.Entities;
using ContableAI.Domain.Enums;
using ContableAI.Infrastructure.Features.Transactions;
using ContableAI.Infrastructure.Persistence;
using ContableAI.Infrastructure.Services;
using Hangfire;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ContableAI.API.Endpoints;

public static class TransactionEndpoints
{
    public static void MapTransactionEndpoints(this WebApplication app)
    {

        app.MapPost("/api/transactions/upload", async (
            HttpContext httpCtx,
            [FromForm] string? bankCode,
            [FromForm] string? companyId,
            ICurrentTenantService  tenant,
            ContableAIDbContext    dbContext,
            IBackgroundJobClient   backgroundJobClient,
            [FromForm] bool? withoutDateFilter = null,
            [FromForm] bool? forceReapplyRules = null) =>
        {
            var files = httpCtx.Request.Form.Files;
            if (files is null || files.Count == 0)
                return Results.BadRequest("No se subió ningún archivo.");

            // Validación rápida y síncrona (tamaño, extensión, magic bytes) — se hace acá, antes de
            // encolar, para devolver 400 inmediato ante un archivo inválido en vez de que el usuario
            // tenga que esperar un ciclo de polling para enterarse. El procesamiento pesado (parseo/
            // OCR/clasificación) se hace en el job de Hangfire (ver UploadBankStatementHandler).
            var stagedRefs = new List<StagedFileRef>();
            foreach (var f in files)
            {
                if (f.Length > UploadBankStatementHandler.MaxFileSizeBytes)
                    return Results.BadRequest($"El archivo '{f.FileName}' supera el máximo permitido de 25 MB.");

                var ext = Path.GetExtension(f.FileName ?? string.Empty);
                if (!UploadBankStatementHandler.AllowedExtensions.Contains(ext))
                    return Results.BadRequest($"Formato no soportado para '{f.FileName}'. Solo se permiten CSV, XLSX y PDF.");

                using var ms = new MemoryStream((int)Math.Max(0, f.Length));
                await f.CopyToAsync(ms);
                var content = ms.ToArray();

                // M-2: validar la firma binaria (magic bytes), no solo la extensión. Los archivos
                // vacíos se dejan pasar acá y se saltean en el handler (Content.Length == 0).
                if (content.Length > 0 && !UploadBankStatementHandler.HasValidSignature(ext, content))
                    return Results.BadRequest($"El archivo '{f.FileName}' no coincide con su extensión (firma binaria inválida).");

                var staged = new StagedUploadFile
                {
                    FileName = f.FileName ?? string.Empty,
                    Content  = content,
                    Length   = f.Length,
                };
                dbContext.StagedUploadFiles.Add(staged);
                stagedRefs.Add(new StagedFileRef(staged.Id, staged.FileName, staged.Length));
            }

            await dbContext.SaveChangesAsync();

            Guid? cId = Guid.TryParse(companyId, out var g) ? g : null;
            var uploadId = Guid.NewGuid();
            var command = new UploadBankStatementCommand(
                uploadId, stagedRefs, cId, bankCode,
                withoutDateFilter ?? false, forceReapplyRules ?? false,
                tenant.StudioTenantId!);

            backgroundJobClient.Enqueue<ISender>(sender => sender.Send(command, default));

            return Results.Accepted(value: new
            {
                UploadId = uploadId,
                Message  = "Procesando el extracto en segundo plano. Esto puede demorar unos minutos.",
            });
        })
        .DisableAntiforgery()
        .WithName("UploadBankStatement")
        .WithTags("Transacciones")
        .WithSummary("Importar y clasificar extractos bancarios (CSV, XLSX, PDF) en segundo plano.")
        .WithDescription("Form-data multipart: files[] (uno o más archivos), bankCode (AUTO | BBVA | GALICIA | ...), companyId (guid). Encola el procesamiento (parseo/OCR/clasificación) como job de Hangfire y responde 202 con un uploadId. El resultado se consulta vía GET /api/transactions/upload/{uploadId}/result.")
        .Produces(202)
        .Produces(400);


        app.MapGet("/api/transactions/upload/{uploadId:guid}/result", async (
            Guid uploadId,
            ICurrentTenantService tenant,
            ContableAIDbContext   dbContext) =>
        {
            var row = await dbContext.UploadJobResults
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.JobId == uploadId.ToString() && r.StudioTenantId == tenant.StudioTenantId);

            // done:false (200, no 404) mientras el job no terminó — evita que el interceptor
            // global de errores del frontend muestre un toast de "no encontrado" en cada poll.
            if (row is null)
                return Results.Ok(new { done = false });

            using var resultDoc = JsonDocument.Parse(row.ResultJson);
            var root = resultDoc.RootElement;

            object? value = root.TryGetProperty("value", out var v) && v.ValueKind != JsonValueKind.Null
                ? v.Clone()
                : null;
            string? error = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String
                ? e.GetString()
                : null;

            return Results.Ok(new
            {
                done       = true,
                isSuccess  = row.IsSuccess,
                statusCode = row.StatusCode,
                error,
                value,
            });
        })
        .WithName("GetUploadResult")
        .WithTags("Transacciones")
        .WithSummary("Consultar el resultado de un job de subida de extracto (polling).")
        .WithDescription("Devuelve { done: false } mientras el job no terminó. Al terminar: { done: true, isSuccess, statusCode, error, value }, donde value tiene la misma forma que la respuesta síncrona de antes.")
        .Produces(200);


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

            // C-3: antes una sola query traía TODAS las filas filtradas (AssignedAccount/mes/año de
            // cada transacción) solo para poblar los dropdowns de filtro — con un estudio grande eso
            // materializaba la tabla entera en cada carga de la grilla. Ahora son 3 queries de
            // valores DISTINCT resueltas en Postgres (el índice IX_BankTransactions_CompanyId_Date
            // cubre el filtro por empresa+fecha); solo el normalizado "Pending"/orden de cuentas se
            // hace en memoria, y sobre un set chico (cuentas distintas), no sobre todas las filas.
            var distinctAccounts = await filterBaseQuery
                .Select(t => t.AssignedAccount)
                .Distinct()
                .ToListAsync();

            var normalizedAccounts = distinctAccounts
                .Select(a => string.IsNullOrWhiteSpace(a) ? "Pending" : a.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(a => a == "Pending" ? 0 : 1)
                .ThenBy(a => a, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var availableMonths = await filterBaseQuery
                .Select(t => t.Date.Month)
                .Distinct()
                .OrderBy(m => m)
                .ToListAsync();

            var availableYears = await filterBaseQuery
                .Select(t => t.Date.Year)
                .Distinct()
                .OrderByDescending(y => y)
                .ToListAsync();

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
            var newKeywords = await CheckManualSuggestionsBatchAsync(
                dbContext, currentTenant.StudioTenantId!,
                [(tx.CompanyId, tx.Description, tx.AssignedAccount)], logger);
            return Results.Ok(new { Transaction = tx, NewSuggestionKeyword = newKeywords.FirstOrDefault() });
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
                // P-6: un solo lote para todos los grupos distintos (antes: ~3 round-trips a
                // la BD por cada grupo de descripción/cuenta).
                var reps = transactions
                    .GroupBy(t => new { t.Description, t.AssignedAccount, t.CompanyId, t.TenantId })
                    .Select(g => g.First())
                    .Select(t => (t.CompanyId, t.Description, t.AssignedAccount))
                    .ToList();

                newSuggestionKeywords = await CheckManualSuggestionsBatchAsync(
                    dbContext, currentTenant.StudioTenantId!, reps, logger);
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
            ContableAIDbContext   dbContext,
            HttpContext           httpContext) =>
        {
            var studioId = tenant.StudioTenantId;

            var studioCompanyIds = await dbContext.Companies
                .AsNoTracking()
                .Where(c => c.StudioTenantId == studioId && c.IsActive)
                .Select(c => c.Id)
                .ToListAsync();

            // P-10: ExecuteDeleteAsync borra directo en SQL sin materializar ni trackear las filas.
            // Al no pasar por SaveChangesAsync tampoco dispara AuditInterceptor (que audita altas/
            // bajas de BankTransaction una por una) — para no perder el rastro de "se borró todo" se
            // agrega abajo UNA fila de auditoría consolidada con la cantidad borrada, en vez de las N
            // filas individuales que generaba el interceptor (que además serían puro ruido acá).
            var deletedCount = await dbContext.BankTransactions
                .Where(t => t.CompanyId.HasValue && studioCompanyIds.Contains(t.CompanyId.Value))
                .ExecuteDeleteAsync();

            if (deletedCount == 0)
                return Results.Ok(new { message = "No había movimientos para limpiar." });

            var userId = httpContext.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "system";
            var userEmail = httpContext.User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value ?? "system";

            dbContext.AuditLogs.Add(new AuditLog
            {
                TenantId   = studioId ?? string.Empty,
                UserId     = userId,
                UserEmail  = userEmail,
                Action     = AuditAction.Deleted.ToString(),
                EntityName = nameof(BankTransaction),
                EntityId   = "BULK",
                Changes    = JsonSerializer.Serialize(new { DeletedCount = deletedCount }),
            });
            await dbContext.SaveChangesAsync();

            return Results.Ok(new { message = $"Se eliminaron {deletedCount} movimientos." });
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

    /// <summary>
    /// P-6: chequeo de sugerencias de reglas en LOTE. La versión anterior corría por cada grupo
    /// distinto de descripción/cuenta del bulk-update (~3 round-trips a la BD por grupo: regla
    /// existente + conteo de manuales + sugerencia existente). Esta versión resuelve N grupos
    /// con 3 consultas fijas y un único SaveChanges, manteniendo la misma semántica:
    /// crea (o reactiva una Rejected) cuando hay ≥ 3 asignaciones manuales del mismo keyword
    /// normalizado a la misma cuenta, y solo devuelve los keywords creados/reactivados.
    /// </summary>
    private static async Task<List<string>> CheckManualSuggestionsBatchAsync(
        ContableAIDbContext db,
        string tenantId,
        IReadOnlyCollection<(Guid? CompanyId, string Description, string? AssignedAccount)> representatives,
        ILogger logger,
        CancellationToken ct = default)
    {
        var candidates = representatives
            .Where(r => r.CompanyId is not null && !string.IsNullOrWhiteSpace(r.AssignedAccount))
            .Select(r => (
                CompanyId: r.CompanyId!.Value,
                Keyword:   KeywordNormalizer.Normalize(r.Description),
                Account:   r.AssignedAccount!))
            .Where(c => !string.IsNullOrWhiteSpace(c.Keyword))
            .DistinctBy(c => (c.CompanyId, c.Keyword))
            .ToList();

        if (candidates.Count == 0) return [];

        var companyIds = candidates.Select(c => c.CompanyId).Distinct().ToList();
        var keywords   = candidates.Select(c => c.Keyword).Distinct().ToList();
        var accounts   = candidates.Select(c => c.Account).Distinct().ToList();

        // Query 1/3 — reglas ya existentes para cualquiera de los pares (empresa, keyword).
        var existingRules = (await db.AccountingRules
                .Where(r => r.CompanyId != null
                         && companyIds.Contains(r.CompanyId.Value)
                         && keywords.Contains(r.Keyword))
                .Select(r => new { r.CompanyId, r.Keyword })
                .ToListAsync(ct))
            .Select(x => (x.CompanyId!.Value, x.Keyword))
            .ToHashSet();

        // Query 2/3 — asignaciones manuales de la ventana para TODAS las empresas/cuentas del
        // lote de una vez; el conteo por keyword normalizado se resuelve en memoria (la
        // normalización no es traducible a SQL, igual que antes).
        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-730));
        var manualRows = await db.BankTransactions
            .Where(t => t.CompanyId != null
                     && companyIds.Contains(t.CompanyId.Value)
                     && t.AssignedAccount != null
                     && accounts.Contains(t.AssignedAccount)
                     && t.ClassificationSource == ClassificationSources.Manual
                     && t.Date >= cutoff)
            .Select(t => new { t.CompanyId, t.AssignedAccount, t.Description })
            .ToListAsync(ct);

        var manualCounts = manualRows
            .GroupBy(r => (CompanyId: r.CompanyId!.Value,
                           Account:   r.AssignedAccount!,
                           Keyword:   KeywordNormalizer.Normalize(r.Description)))
            .ToDictionary(g => g.Key, g => g.Count());

        // Query 3/3 — sugerencias ya registradas para esos pares (empresa, keyword).
        var existingSuggestions = (await db.RuleSuggestions
                .Where(s => s.CompanyId != null
                         && companyIds.Contains(s.CompanyId.Value)
                         && keywords.Contains(s.Keyword))
                .ToListAsync(ct))
            .ToDictionary(s => (s.CompanyId!.Value, s.Keyword));

        var newKeywords = new List<string>();
        var dirty       = false;

        foreach (var c in candidates)
        {
            if (existingRules.Contains((c.CompanyId, c.Keyword)))
            {
                logger.LogDebug("Suggestion check: Skip — Keyword={Keyword}, Reason=RuleAlreadyExists", c.Keyword);
                continue;
            }

            manualCounts.TryGetValue((c.CompanyId, c.Account, c.Keyword), out var count);
            if (count < 3) continue;

            if (existingSuggestions.TryGetValue((c.CompanyId, c.Keyword), out var existing))
            {
                if (existing.Status == SuggestionStatus.Rejected)
                {
                    existing.Status           = SuggestionStatus.Pending;
                    existing.SuggestedAccount = c.Account;
                    existing.Frequency        = count;
                    newKeywords.Add(c.Keyword);
                    dirty = true;
                    logger.LogDebug("Suggestion check: Reactivated — Keyword={Keyword}, Account={Account}, Frequency={Frequency}", c.Keyword, c.Account, count);
                }
                else if (existing.Status == SuggestionStatus.Pending && existing.Frequency < count)
                {
                    existing.Frequency = count;
                    dirty = true;
                    logger.LogDebug("Suggestion check: Updated — Keyword={Keyword}, NewFrequency={NewFrequency}", c.Keyword, count);
                }
                continue;
            }

            db.RuleSuggestions.Add(new RuleSuggestion
            {
                TenantId         = tenantId,
                CompanyId        = c.CompanyId,
                Keyword          = c.Keyword,
                SuggestedAccount = c.Account,
                Frequency        = count,
                Status           = SuggestionStatus.Pending
            });
            newKeywords.Add(c.Keyword);
            dirty = true;
            logger.LogDebug("Suggestion check: Created — Keyword={Keyword}, Account={Account}, Frequency={Frequency}", c.Keyword, c.Account, count);
        }

        if (dirty)
            await db.SaveChangesAsync(ct);

        return newKeywords;
    }
}
