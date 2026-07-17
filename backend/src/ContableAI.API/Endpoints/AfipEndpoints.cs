using ContableAI.Domain.Constants;
using ContableAI.Domain.Entities;
using ContableAI.Infrastructure.Features.Afip;
using ContableAI.Infrastructure.Persistence;
using ContableAI.Infrastructure.Services;
using Hangfire;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ContableAI.API.Endpoints;

public static class AfipEndpoints
{
    public static void MapAfipEndpoints(this WebApplication app)
    {
        // -- POST /api/companies/{companyId}/afip/upload --
        // Parsea PDFs, persiste AfipVouchers y encola el matching job.
        app.MapPost("/api/companies/{companyId:guid}/afip/upload", async (
            Guid companyId,
            HttpContext httpCtx,
            [FromServices] IAfipParserService afipParser,
            [FromServices] ContableAIDbContext dbContext,
            [FromServices] IBackgroundJobClient backgroundJobClient) =>
        {
            var files = httpCtx.Request.Form.Files;
            if (files == null || files.Count == 0)
                return Results.BadRequest("No se subió ningún archivo de AFIP.");

            // Verificar que la empresa existe
            var company = await dbContext.Companies.FindAsync(companyId);
            if (company == null)
                return Results.NotFound("Empresa no encontrada.");

            var presentations = new List<AfipPresentation>();
            foreach (var file in files)
            {
                if (file.Length == 0) continue;
                using var stream = file.OpenReadStream();
                presentations.AddRange(afipParser.ParsePdf(stream));
            }

            if (presentations.Count == 0)
                return Results.BadRequest("No se pudo extraer información de los PDFs. Verificá que sean comprobantes VEP válidos.");

            // Pre-cargar claves existentes en BD para esta empresa
            var existingKeys = await dbContext.AfipVouchers
                .Where(v => v.CompanyId == companyId)
                .Select(v => new { v.Date, v.Amount, v.TaxName })
                .ToListAsync();

            // HashSet unificado: detecta duplicados en BD y dentro del mismo lote
            var seenKeys = new HashSet<(DateOnly, decimal, string?)>(
                existingKeys.Select(e => (e.Date, e.Amount, e.TaxName)));

            int addedCount = 0;
            var skippedDuplicates = new List<object>();
            foreach (var p in presentations)
            {
                if (!seenKeys.Add((p.Date, p.Amount, p.TaxName)))
                {
                    skippedDuplicates.Add(new
                    {
                        date        = p.Date,
                        amount      = p.Amount,
                        description = p.TaxName ?? string.Empty,
                    });
                    continue;
                }

                dbContext.AfipVouchers.Add(new AfipVoucher
                {
                    TenantId        = company.StudioTenantId ?? string.Empty,
                    StudioTenantId  = company.StudioTenantId,
                    CompanyId       = companyId,
                    Date            = p.Date,
                    Amount          = p.Amount,
                    TaxName         = p.TaxName,
                });
                addedCount++;
            }

            if (addedCount > 0)
                await dbContext.SaveChangesAsync();

            // Encolar el job de cruce (corre aunque addedCount sea 0 — puede haber nuevas txs)
            backgroundJobClient.Enqueue<AfipMatchingJob>(job => job.RunAsync(companyId));

            return Results.Ok(new { added = addedCount, skippedDuplicates });
        })
        .DisableAntiforgery()
        .RequireRateLimiting("afip")
        .WithName("UploadAfipVouchers")
        .WithTags("AFIP")
        .WithSummary("Subir comprobantes VEP de AFIP (PDF) y disparar cruce automático.")
        .WithDescription("Form-data multipart: files[] (uno o más PDFs VEP). Persiste los vouchers nuevos en BD y encola el job de matching en Hangfire. Retorna la cantidad de vouchers nuevos agregados.")
        .Produces(200)
        .Produces(400)
        .Produces(404);

        // -- GET /api/companies/{companyId}/afip/vouchers --
        // Lista los comprobantes AFIP persistidos con su estado de cruce.
        app.MapGet("/api/companies/{companyId:guid}/afip/vouchers", async (
            Guid companyId,
            ContableAIDbContext dbContext) =>
        {
            var vouchers = await dbContext.AfipVouchers
                .Where(v => v.CompanyId == companyId)
                .OrderByDescending(v => v.Date)
                .Select(v => new
                {
                    v.Id,
                    v.Date,
                    v.Amount,
                    v.TaxName,
                    v.IsMatched,
                    v.MatchedTransactionId,
                })
                .ToListAsync();

            return Results.Ok(vouchers);
        })
        .WithName("GetAfipVouchers")
        .WithTags("AFIP")
        .WithSummary("Listar comprobantes VEP de AFIP para una empresa.")
        .WithDescription("Retorna todos los comprobantes AFIP cargados para la empresa con su estado de cruce (isMatched).")
        .Produces(200);

        // -- GET /api/companies/{companyId}/afip/combination-suggestions --
        // Calcula (sin persistir ni aplicar nada) las combinaciones de VEPs pendientes cuya
        // sumatoria coincide exactamente con débitos AFIP sin conciliar.
        app.MapGet("/api/companies/{companyId:guid}/afip/combination-suggestions", async (
            Guid companyId,
            [FromServices] AfipCombinationService combinationService,
            CancellationToken ct) =>
        {
            var suggestions = await combinationService.ComputeSuggestionsAsync(companyId, ct);
            return Results.Ok(suggestions);
        })
        .WithName("GetAfipCombinationSuggestions")
        .WithTags("AFIP")
        .WithSummary("Sugerir combinaciones de VEPs que suman el importe de un movimiento bancario.")
        .WithDescription("Un débito a AFIP puede corresponder al pago agrupado de 2+ VEPs (ej. IVA + IIBB). Devuelve, por cada movimiento pendiente de cruce, las combinaciones de VEPs pendientes cuya sumatoria coincide exactamente. Solo sugiere: la aplicación requiere confirmación explícita vía apply-combination.")
        .Produces(200);

        // -- POST /api/companies/{companyId}/afip/apply-combination --
        // Aplica una combinación PREVIAMENTE CONFIRMADA POR EL USUARIO: nunca se llama de
        // forma automática. Revalida todo en servidor antes de asentar.
        app.MapPost("/api/companies/{companyId:guid}/afip/apply-combination", async (
            Guid companyId,
            ApplyAfipCombinationRequest request,
            [FromServices] ContableAIDbContext dbContext,
            [FromServices] IAccountNameResolver accountResolver,
            CancellationToken ct) =>
        {
            var voucherIds = (request.VoucherIds ?? []).Distinct().ToList();
            if (voucherIds.Count < 2)
                return Results.BadRequest("Una combinación requiere al menos 2 VEPs (el cruce 1:1 es automático).");

            var tx = await dbContext.BankTransactions
                .FirstOrDefaultAsync(t => t.Id == request.TransactionId && t.CompanyId == companyId, ct);
            if (tx == null)
                return Results.NotFound("Movimiento bancario no encontrado para esta empresa.");
            if (tx.JournalEntryId != null)
                return Results.Conflict("El movimiento ya fue asentado; no se puede aplicar la combinación.");

            // Guard multi-moneda (defensa en profundidad): la UI nunca ofrece combos para USD,
            // pero el endpoint no debe confiar en la UI. Los VEPs de ARCA son siempre pesos.
            if (tx.Currency != Currencies.Ars)
                return Results.BadRequest("No se puede cruzar un movimiento en USD contra VEPs de AFIP (ARS).");

            var vouchers = await dbContext.AfipVouchers
                .Where(v => voucherIds.Contains(v.Id) && v.CompanyId == companyId)
                .ToListAsync(ct);
            if (vouchers.Count != voucherIds.Count)
                return Results.NotFound("Uno o más VEPs no existen para esta empresa.");
            if (vouchers.Any(v => v.IsMatched))
                return Results.Conflict("Uno o más VEPs ya fueron conciliados con otro movimiento.");

            var sum = vouchers.Sum(v => v.Amount);
            if (sum != tx.Amount)
                return Results.BadRequest(
                    $"La sumatoria de los VEPs ({sum:0.00}) no coincide con el importe del movimiento ({tx.Amount:0.00}).");

            // Canonicalizar los nombres de impuesto contra el plan de cuentas del estudio
            // (mismo criterio que el cruce 1:1) y persistirlos: el generador de asientos usa
            // voucher.TaxName como cuenta de cada línea del desglose.
            var studioTenantId = await dbContext.Companies
                .Where(c => c.Id == companyId)
                .Select(c => c.StudioTenantId)
                .FirstOrDefaultAsync(ct);
            Guid? studioGuid = Guid.TryParse(studioTenantId, out var sg) ? sg : null;
            var accountMap = await accountResolver.BuildMapAsync(studioGuid, ct);

            foreach (var voucher in vouchers)
            {
                voucher.TaxName = accountMap.Resolve(voucher.TaxName);
                voucher.IsMatched = true;
                voucher.MatchedTransactionId = tx.Id;
            }

            // Cuenta visible en la grilla: los impuestos del desglose (el asiento real se
            // genera con una línea por VEP vía ClassificationSources.AfipComboMatch).
            var accountLabel = string.Join(" + ", vouchers
                .GroupBy(v => v.TaxName)
                .OrderByDescending(g => g.Sum(v => v.Amount))
                .Select(g => g.Key));

            tx.Assign(accountLabel, null, false, ClassificationSources.AfipComboMatch);
            tx.Notes = $"Cruce múltiple AFIP ({vouchers.Count} VEPs): " + string.Join(" + ", vouchers
                .OrderByDescending(v => v.Amount)
                .Select(v => $"{v.TaxName} ${v.Amount:N2}"));

            await dbContext.SaveChangesAsync(ct);

            return Results.Ok(new
            {
                transactionId  = tx.Id,
                assignedAccount = accountLabel,
                vouchersMatched = vouchers.Count,
            });
        })
        .WithName("ApplyAfipCombination")
        .WithTags("AFIP")
        .WithSummary("Aplicar una combinación de VEPs confirmada por el usuario a un movimiento bancario.")
        .WithDescription("Marca los VEPs como conciliados contra el movimiento y clasifica la transacción como AfipComboMatch (el asiento se genera desglosado, una línea por impuesto). Valida en servidor que la sumatoria coincida exactamente y que nada haya sido conciliado en el medio.")
        .Produces(200)
        .Produces(400)
        .Produces(404)
        .Produces(409);

        // -- POST /api/companies/{companyId}/afip/rematch --
        // Re-dispara el matching job manualmente (útil tras subir nuevos extractos).
        app.MapPost("/api/companies/{companyId:guid}/afip/rematch", (
            Guid companyId,
            [FromServices] IBackgroundJobClient backgroundJobClient) =>
        {
            var jobId = backgroundJobClient.Enqueue<AfipMatchingJob>(job => job.RunAsync(companyId));
            return Results.Accepted(value: new { JobId = jobId });
        })
        .WithName("RematchAfipVouchers")
        .WithTags("AFIP")
        .WithSummary("Re-disparar cruce AFIP para una empresa.")
        .WithDescription("Encola el job de matching en Hangfire. Útil para re-intentar el cruce luego de subir nuevos extractos bancarios.")
        .Produces(202);
    }
}
