using ContableAI.Domain.Entities;
using ContableAI.Infrastructure.Persistence;
using ContableAI.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OfficeOpenXml;

namespace ContableAI.API.Endpoints;

public static class ChartOfAccountsEndpoints
{
    public static void MapChartOfAccountsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/chart-of-accounts", async (
            ICurrentTenantService currentTenant,
            ContableAIDbContext   dbContext,
            [FromQuery] bool      includeInactive = false) =>
        {
            Guid.TryParse(currentTenant.StudioTenantId, out var studioGuid);

            var query = dbContext.ChartOfAccounts
                .AsNoTracking()
                .Where(a => a.StudioTenantId == null || a.StudioTenantId == studioGuid);

            if (!includeInactive)
                query = query.Where(a => a.IsActive);

            var accounts = await query
                .OrderBy(a => a.StudioTenantId == null ? 0 : 1) // globales primero
                .ThenBy(a => a.Name)
                .Select(a => new { a.Id, a.Name, a.ExternalCode, a.IsActive, IsGlobal = a.StudioTenantId == null })
                .ToListAsync();

            return Results.Ok(accounts);
        })
        .WithName("GetChartOfAccounts")
        .WithTags("Plan de Cuentas")
        .WithSummary("Listar cuentas contables (globales + propias del estudio).")
        .WithDescription("Devuelve { id, name, isActive, isGlobal, externalCode }. Por defecto solo devuelve cuentas activas. Usar ?includeInactive=true para ver todas. Las cuentas globales aparecen primero.")
        .Produces(200);

        app.MapPost("/api/chart-of-accounts", async (
            CreateChartOfAccountRequest req,
            ICurrentTenantService        currentTenant,
            ContableAIDbContext          dbContext) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest("El nombre de la cuenta es obligatorio.");

            if (!Guid.TryParse(currentTenant.StudioTenantId, out var studioGuid))
                return Results.Unauthorized();

            var existing = await dbContext.ChartOfAccounts
                .AnyAsync(a => a.Name == req.Name.Trim()
                            && (a.StudioTenantId == null || a.StudioTenantId == studioGuid));

            if (existing)
                return Results.Conflict("Ya existe una cuenta con ese nombre.");

            var account = new ChartOfAccount
            {
                Name           = req.Name.Trim(),
                StudioTenantId = studioGuid,
                ExternalCode   = string.IsNullOrWhiteSpace(req.ExternalCode) ? null : req.ExternalCode.Trim(),
            };

            dbContext.ChartOfAccounts.Add(account);
            await dbContext.SaveChangesAsync();

            return Results.Created($"/api/chart-of-accounts/{account.Id}",
                new { account.Id, account.Name, account.ExternalCode, IsGlobal = false });
        })
        .WithName("CreateChartOfAccount")
        .WithTags("Plan de Cuentas")
        .WithSummary("Agregar una cuenta al plan de cuentas del estudio.")
        .WithDescription("Body: { name: string }. No puede tener el mismo nombre que una cuenta global o ya existente del estudio (case-sensitive). Devuelve 201 con { id, name, isGlobal: false }.")
        .Produces(201)
        .Produces<ProblemDetails>(409);

        // ── PATCH — editar código externo (globales y propias del estudio) ────
        app.MapPatch("/api/chart-of-accounts/{id:guid}/external-code", async (
            Guid                         id,
            UpdateChartOfAccountRequest  req,
            ICurrentTenantService        currentTenant,
            ContableAIDbContext          dbContext) =>
        {
            if (!Guid.TryParse(currentTenant.StudioTenantId, out var studioGuid))
                return Results.Unauthorized();

            var account = await dbContext.ChartOfAccounts.FindAsync(id);
            if (account == null) return Results.NotFound();

            // Cuentas de otro estudio: no permitido
            if (account.StudioTenantId.HasValue && account.StudioTenantId != studioGuid)
                return Results.Forbid();

            account.ExternalCode = string.IsNullOrWhiteSpace(req.ExternalCode) ? null : req.ExternalCode.Trim();
            await dbContext.SaveChangesAsync();

            return Results.Ok(new { account.Id, account.Name, account.ExternalCode, IsGlobal = account.StudioTenantId == null });
        })
        .WithName("UpdateChartOfAccountExternalCode")
        .WithTags("Plan de Cuentas")
        .WithSummary("Asignar o borrar el código externo de una cuenta contable.")
        .WithDescription("Body: { externalCode: string | null }. Funciona tanto para cuentas globales como del estudio. Devuelve { id, name, externalCode, isGlobal }.")
        .Produces(200)
        .Produces<ProblemDetails>(403)
        .Produces<ProblemDetails>(404);

        // ── DELETE — solo se pueden eliminar cuentas propias sin dependencias ──
        app.MapDelete("/api/chart-of-accounts/{id:guid}", async (
            Guid                  id,
            ICurrentTenantService currentTenant,
            ContableAIDbContext   dbContext) =>
        {
            var account = await dbContext.ChartOfAccounts.FindAsync(id);
            if (account == null) return Results.NotFound();

            if (!Guid.TryParse(currentTenant.StudioTenantId, out var studioGuid)
                || account.StudioTenantId != studioGuid)
                return Results.Forbid();

            // Obtener empresas del estudio para acotar las búsquedas
            // Company.StudioTenantId es string, no Guid
            var companyIds = await dbContext.Companies
                .Where(c => c.StudioTenantId == currentTenant.StudioTenantId)
                .Select(c => c.Id)
                .ToListAsync();

            var usedBy = new List<string>();

            if (companyIds.Count > 0)
            {
                if (await dbContext.BankTransactions.AnyAsync(t =>
                        t.AssignedAccount == account.Name &&
                        t.CompanyId != null &&
                        companyIds.Contains(t.CompanyId.Value)))
                    usedBy.Add("movimientos bancarios");

                if (await (
                        from l in dbContext.JournalEntryLines
                        join j in dbContext.JournalEntries on l.JournalEntryId equals j.Id
                        where l.Account == account.Name &&
                              j.CompanyId != null &&
                              companyIds.Contains(j.CompanyId.Value)
                        select l.Id
                    ).AnyAsync())
                    usedBy.Add("asientos contables");

                if (await dbContext.AccountingRules.AnyAsync(r =>
                        r.TargetAccount == account.Name &&
                        r.CompanyId != null &&
                        companyIds.Contains(r.CompanyId.Value)))
                    usedBy.Add("reglas de clasificación");
            }

            if (usedBy.Count > 0)
                return Results.Problem(
                    title:      "Cuenta en uso",
                    detail:     $"No se puede eliminar \"{account.Name}\" porque está referenciada en: {string.Join(", ", usedBy)}.",
                    statusCode: 409);

            dbContext.ChartOfAccounts.Remove(account);
            await dbContext.SaveChangesAsync();
            return Results.NoContent();
        })
        .WithName("DeleteChartOfAccount")
        .WithTags("Plan de Cuentas")
        .WithSummary("Eliminar una cuenta del plan de cuentas del estudio.")
        .WithDescription("Solo se pueden eliminar cuentas propias sin dependencias activas. Devuelve 409 si la cuenta está en uso en movimientos, asientos o reglas.")
        .Produces(204)
        .Produces<ProblemDetails>(403)
        .Produces<ProblemDetails>(404)
        .Produces<ProblemDetails>(409);

        // ── PATCH — desactivar cuenta (soft delete) ────────────────────────────
        app.MapPatch("/api/chart-of-accounts/{id:guid}/deactivate", async (
            Guid                  id,
            ICurrentTenantService currentTenant,
            ContableAIDbContext   dbContext) =>
        {
            var account = await dbContext.ChartOfAccounts.FindAsync(id);
            if (account == null) return Results.NotFound();

            if (!Guid.TryParse(currentTenant.StudioTenantId, out var studioGuid)
                || account.StudioTenantId != studioGuid)
                return Results.Forbid();

            account.IsActive = false;
            await dbContext.SaveChangesAsync();

            return Results.Ok(new { account.Id, account.Name, account.ExternalCode, account.IsActive, IsGlobal = account.StudioTenantId == null });
        })
        .WithName("DeactivateChartOfAccount")
        .WithTags("Plan de Cuentas")
        .WithSummary("Desactivar una cuenta contable (soft delete).")
        .WithDescription("La cuenta deja de aparecer en los selectores pero conserva sus relaciones históricas. Solo se puede desactivar cuentas propias del estudio.")
        .Produces(200)
        .Produces<ProblemDetails>(403)
        .Produces<ProblemDetails>(404);

        // ── PATCH — reactivar cuenta ──────────────────────────────────────────
        app.MapPatch("/api/chart-of-accounts/{id:guid}/activate", async (
            Guid                  id,
            ICurrentTenantService currentTenant,
            ContableAIDbContext   dbContext) =>
        {
            var account = await dbContext.ChartOfAccounts.FindAsync(id);
            if (account == null) return Results.NotFound();

            if (!Guid.TryParse(currentTenant.StudioTenantId, out var studioGuid)
                || account.StudioTenantId != studioGuid)
                return Results.Forbid();

            account.IsActive = true;
            await dbContext.SaveChangesAsync();

            return Results.Ok(new { account.Id, account.Name, account.ExternalCode, account.IsActive, IsGlobal = account.StudioTenantId == null });
        })
        .WithName("ActivateChartOfAccount")
        .WithTags("Plan de Cuentas")
        .WithSummary("Reactivar una cuenta contable previamente desactivada.")
        .WithDescription("Devuelve la cuenta con IsActive = true. Solo se puede operar sobre cuentas propias del estudio.")
        .Produces(200)
        .Produces<ProblemDetails>(403)
        .Produces<ProblemDetails>(404);

        // ── POST — importar masivamente desde un Excel (xlsx) ──
        app.MapPost("/api/chart-of-accounts/import", async (
            HttpContext context,
            ICurrentTenantService currentTenant,
            ContableAIDbContext dbContext) =>
        {
            if (!Guid.TryParse(currentTenant.StudioTenantId, out var studioGuid))
                return Results.Unauthorized();

            var form = await context.Request.ReadFormAsync();
            var file = form.Files.GetFile("file");

            if (file == null || file.Length == 0)
                return Results.BadRequest("No se envió ningún archivo válido.");

            var ext = Path.GetExtension(file.FileName);
            if (!ext.Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest("El archivo debe ser un Excel (.xlsx).");

            using var stream = new MemoryStream();
            await file.CopyToAsync(stream);
            
            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
            using var package = new ExcelPackage(stream);
            var sheet = package.Workbook.Worksheets.FirstOrDefault();

            if (sheet == null)
                return Results.BadRequest("El Excel está vacío o no se pudo leer la hoja.");

            int lastRow = sheet.Dimension?.End.Row ?? 0;
            if (lastRow < 2)
                return Results.BadRequest("El Excel no tiene filas para importar (verificá que tenga encabezados y datos).");

            // Buscar en la cabecera (Fila 1) las columnas relevantes
            int nameColIndex = -1;
            int codeColIndex = -1;
            int lastCol = sheet.Dimension!.End.Column;

            for (int col = 1; col <= lastCol; col++)
            {
                var headerText = (sheet.Cells[1, col].Value?.ToString() ?? "").Trim().ToLower();
                if (headerText.Contains("nombre")) nameColIndex = col;
                if (headerText.Contains("codigo") || headerText.Contains("código") || headerText.Contains("code")) codeColIndex = col;
            }

            if (nameColIndex == -1) // La de nombre es mínima y obligatoria
                return Results.BadRequest("No se encontró una columna que contenga 'Nombre' en los encabezados.");

            var currentAccountsQuery = await dbContext.ChartOfAccounts
                .Where(a => a.StudioTenantId == null || a.StudioTenantId == studioGuid)
                .Select(a => new { a.Name })
                .ToListAsync();

            var existingNames = currentAccountsQuery.Select(a => a.Name.ToLowerInvariant()).ToHashSet();
            
            int addedCount = 0;
            var accountsToAdd = new List<ChartOfAccount>();

            for (int row = 2; row <= lastRow; row++)
            {
                var rawName = sheet.Cells[row, nameColIndex].Text?.Trim();
                if (string.IsNullOrWhiteSpace(rawName)) continue;

                if (existingNames.Contains(rawName.ToLowerInvariant()))
                    continue;

                string? rawCode = null;
                if (codeColIndex != -1)
                {
                    rawCode = sheet.Cells[row, codeColIndex].Text?.Trim();
                }

                existingNames.Add(rawName.ToLowerInvariant());

                accountsToAdd.Add(new ChartOfAccount
                {
                    Name = rawName,
                    ExternalCode = string.IsNullOrWhiteSpace(rawCode) ? null : rawCode,
                    StudioTenantId = studioGuid
                });
                
                addedCount++;
            }

            if (accountsToAdd.Count > 0)
            {
                await dbContext.ChartOfAccounts.AddRangeAsync(accountsToAdd);
                await dbContext.SaveChangesAsync();
            }

            return Results.Ok(new { message = $"Se importaron {addedCount} cuentas correctamente.", imported = addedCount });
        })
        .WithName("ImportChartOfAccounts")
        .WithTags("Plan de Cuentas")
        .WithSummary("Importar cuentas contables masivamente desde un archivo Excel.")
        .WithDescription("Acepta Content-Type multipart/form-data con un campo 'file'. Busca la columna 'Nombre' y, opcionalmente, 'Codigo' en la primera fila.")
        .DisableAntiforgery(); // Requerido en minimal APIs para uploads via IFormFile si no se configuran tokens
    }
}
