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
