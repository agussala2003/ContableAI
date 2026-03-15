using ContableAI.Infrastructure.Persistence;
using ContableAI.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ContableAI.API.Endpoints;

public static class AfipEndpoints
{
    public static void MapAfipEndpoints(this WebApplication app)
    {
        // -- POST /api/afip/match -- cruce con VEPs de AFIP (uno o mas PDFs) --
        app.MapPost("/api/afip/match", async (
            HttpContext httpCtx,
            [FromForm] string? companyId,
            [FromServices] IAfipParserService afipParser,
            [FromServices] ContableAIDbContext dbContext) =>
        {
            var files = httpCtx.Request.Form.Files;
            if (files == null || files.Count == 0)
                return Results.BadRequest("No se subio ningun archivo de AFIP.");

            var allPresentations = new List<AfipPresentation>();
            foreach (var file in files)
            {
                if (file.Length == 0) continue;
                using var stream = file.OpenReadStream();
                allPresentations.AddRange(afipParser.ParsePdf(stream));
            }

            if (allPresentations.Count == 0)
                return Results.BadRequest("No se pudo extraer informacion de los PDFs subidos. Verifica que sean comprobantes VEP validos.");

            var pendingQuery = dbContext.BankTransactions.Where(t => t.NeedsTaxMatching);
            if (!string.IsNullOrWhiteSpace(companyId) && Guid.TryParse(companyId, out var afipCmpId))
                pendingQuery = pendingQuery.Where(t => t.CompanyId == afipCmpId);

            var pendingTxs = await pendingQuery.ToListAsync();
            int matchesFound = 0;

            foreach (var afip in allPresentations)
            {
                var matchingBankTx = pendingTxs.FirstOrDefault(tx =>
                    tx.NeedsTaxMatching &&
                    tx.Amount == afip.Amount &&
                    Math.Abs(tx.Date.DayNumber - afip.Date.DayNumber) <= 2);

                if (matchingBankTx != null)
                {
                    matchingBankTx.Assign(afip.TaxName, null, false, "AFIP Match");
                    matchesFound++;
                }
            }

            if (matchesFound > 0)
                await dbContext.SaveChangesAsync();

            return Results.Ok(new
            {
                TotalPresentationsRead = allPresentations.Count,
                SuccessfulMatches      = matchesFound,
                StillPending           = pendingTxs.Count(t => t.NeedsTaxMatching),
            });
        })
        .DisableAntiforgery()
        .RequireRateLimiting("afip")
        .WithName("MatchAfipPresentations")
        .WithTags("AFIP")
        .WithSummary("Cruzar transacciones con comprobantes VEP de AFIP (PDF).")
        .WithDescription("Form-data multipart: files[] (uno o más PDFs VEP), companyId (guid, opcional). Extrae fecha y monto pagado de cada VEP y los cruza contra transacciones con NeedsTaxMatching = true (tolerancia ±2 días). Soporta comprobantes pagados y pendientes.")
        .Produces(200);
    }
}
