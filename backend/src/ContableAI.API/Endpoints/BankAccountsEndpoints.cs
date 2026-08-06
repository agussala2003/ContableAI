using ContableAI.API.Common;
using ContableAI.Domain.Constants;
using ContableAI.Domain.Entities;
using ContableAI.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ContableAI.API.Endpoints;

/// <summary>
/// ABM de las cuentas bancarias de una empresa (F1 — multi-cuenta).
///
/// El aislamiento por estudio lo resuelven íntegramente los Global Query Filters de
/// <c>Company</c> y <c>BankAccount</c>: una empresa o una cuenta de otro estudio simplemente no
/// existe para estas consultas, y el endpoint responde 404 sin confirmar lo contrario.
/// </summary>
public static class BankAccountsEndpoints
{
    public static void MapBankAccountsEndpoints(this WebApplication app)
    {
        // ── Listar ────────────────────────────────────────────────────────────
        app.MapGet("/api/companies/{companyId:guid}/bank-accounts", async (
            Guid                companyId,
            ContableAIDbContext dbContext,
            [FromQuery] bool    includeInactive = false) =>
        {
            if (!await dbContext.Companies.AnyAsync(c => c.Id == companyId))
                return Results.NotFound("Empresa no encontrada.");

            var query = dbContext.BankAccounts.AsNoTracking()
                .Where(a => a.CompanyId == companyId);

            if (!includeInactive)
                query = query.Where(a => a.IsActive);

            var accounts = await query
                .OrderBy(a => a.Currency == Currencies.Ars ? 0 : 1)
                .ThenBy(a => a.Alias)
                .Select(a => ToResponse(a))
                .ToListAsync();

            return Results.Ok(accounts);
        })
        .WithName("GetBankAccounts")
        .WithTags("Cuentas Bancarias")
        .WithSummary("Listar las cuentas bancarias de una empresa.")
        .WithDescription("Query param: includeInactive (por defecto false). Ordena primero las cuentas en pesos.")
        .Produces<List<BankAccountResponse>>(200)
        .Produces(404);

        // ── Crear ─────────────────────────────────────────────────────────────
        app.MapPost("/api/companies/{companyId:guid}/bank-accounts", async (
            Guid                   companyId,
            SaveBankAccountRequest req,
            ContableAIDbContext    dbContext) =>
        {
            var company = await dbContext.Companies.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == companyId);

            if (company is null)
                return Results.NotFound("Empresa no encontrada.");

            if (Validate(req) is { } error)
                return error;

            var normalized = NormalizeNumber(req.AccountNumber);

            if (await NumberAlreadyUsedAsync(dbContext, companyId, normalized, excludingId: null))
                return Results.Conflict("Ya existe una cuenta con ese número en esta empresa.");

            var account = new BankAccount
            {
                CompanyId         = companyId,
                Alias             = req.Alias.Trim(),
                AccountNumber     = Clean(req.AccountNumber),
                NormalizedNumber  = normalized,
                Cbu               = NormalizeNumber(req.Cbu),
                BankCode          = Clean(req.BankCode)?.ToUpperInvariant(),
                Currency          = req.Currency.Trim().ToUpperInvariant(),
                ContraAccountName = Clean(req.ContraAccountName) ?? string.Empty,
                ChartOfAccountId  = req.ChartOfAccountId,
                IsActive          = true,
                // Desnormalizado desde la empresa: es el ancla del filtro global.
                StudioTenantId    = company.StudioTenantId,
            };

            dbContext.BankAccounts.Add(account);
            await dbContext.SaveChangesAsync();
            await SyncLegacyCompanyFieldsAsync(dbContext, companyId);

            return Results.Created($"/api/bank-accounts/{account.Id}", ToResponse(account));
        })
        .RequireAuthorization(AuthorizationPolicies.RequireStudioOwner)
        .WithName("CreateBankAccount")
        .WithTags("Cuentas Bancarias")
        .WithSummary("Dar de alta una cuenta bancaria en una empresa.")
        .WithDescription("Body: { alias, accountNumber?, cbu?, bankCode?, currency, contraAccountName? }. El número se guarda además normalizado a dígitos para el enrutamiento del OCR y es único por empresa. Sin contraAccountName la cuenta queda provisional: recibe movimientos pero todavía no puede asentarlos.")
        .Produces<BankAccountResponse>(201)
        .Produces(400)
        .Produces(404)
        .Produces(409);

        // ── Editar ────────────────────────────────────────────────────────────
        app.MapPut("/api/bank-accounts/{id:guid}", async (
            Guid                   id,
            SaveBankAccountRequest req,
            ContableAIDbContext    dbContext) =>
        {
            var account = await dbContext.BankAccounts.FirstOrDefaultAsync(a => a.Id == id);
            if (account is null)
                return Results.NotFound("Cuenta bancaria no encontrada.");

            if (Validate(req) is { } error)
                return error;

            var normalized = NormalizeNumber(req.AccountNumber);

            if (await NumberAlreadyUsedAsync(dbContext, account.CompanyId, normalized, excludingId: id))
                return Results.Conflict("Ya existe otra cuenta con ese número en esta empresa.");

            account.Alias             = req.Alias.Trim();
            account.AccountNumber     = Clean(req.AccountNumber);
            account.NormalizedNumber  = normalized;
            account.Cbu               = NormalizeNumber(req.Cbu);
            account.BankCode          = Clean(req.BankCode)?.ToUpperInvariant();
            account.Currency          = req.Currency.Trim().ToUpperInvariant();
            account.ContraAccountName = Clean(req.ContraAccountName) ?? string.Empty;
            account.ChartOfAccountId  = req.ChartOfAccountId;

            await dbContext.SaveChangesAsync();
            await SyncLegacyCompanyFieldsAsync(dbContext, account.CompanyId);

            return Results.Ok(ToResponse(account));
        })
        .RequireAuthorization(AuthorizationPolicies.RequireStudioOwner)
        .WithName("UpdateBankAccount")
        .WithTags("Cuentas Bancarias")
        .WithSummary("Editar una cuenta bancaria.")
        .Produces<BankAccountResponse>(200)
        .Produces(400)
        .Produces(404)
        .Produces(409);

        // ── Baja lógica ───────────────────────────────────────────────────────
        app.MapPatch("/api/bank-accounts/{id:guid}/deactivate", async (
            Guid id, ContableAIDbContext dbContext) =>
        {
            var account = await dbContext.BankAccounts.FirstOrDefaultAsync(a => a.Id == id);
            if (account is null) return Results.NotFound();

            account.IsActive = false;
            await dbContext.SaveChangesAsync();
            await SyncLegacyCompanyFieldsAsync(dbContext, account.CompanyId);

            return Results.Ok(ToResponse(account));
        })
        .RequireAuthorization(AuthorizationPolicies.RequireStudioOwner)
        .WithName("DeactivateBankAccount")
        .WithTags("Cuentas Bancarias")
        .WithSummary("Dar de baja una cuenta bancaria (baja lógica).")
        .WithDescription("Nunca se borra físicamente: los movimientos y asientos ya generados siguen apuntando a ella.")
        .Produces<BankAccountResponse>(200)
        .Produces(404);

        app.MapPatch("/api/bank-accounts/{id:guid}/activate", async (
            Guid id, ContableAIDbContext dbContext) =>
        {
            var account = await dbContext.BankAccounts.FirstOrDefaultAsync(a => a.Id == id);
            if (account is null) return Results.NotFound();

            account.IsActive = true;
            await dbContext.SaveChangesAsync();
            await SyncLegacyCompanyFieldsAsync(dbContext, account.CompanyId);

            return Results.Ok(ToResponse(account));
        })
        .RequireAuthorization(AuthorizationPolicies.RequireStudioOwner)
        .WithName("ActivateBankAccount")
        .WithTags("Cuentas Bancarias")
        .WithSummary("Reactivar una cuenta bancaria dada de baja.")
        .Produces<BankAccountResponse>(200)
        .Produces(404);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static IResult? Validate(SaveBankAccountRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Alias))
            return Results.BadRequest("El alias de la cuenta es obligatorio.");

        if (!Currencies.IsSupported(req.Currency?.Trim().ToUpperInvariant()))
            return Results.BadRequest("Moneda inválida. Valores soportados: ARS, USD.");

        return null;
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Deja solo los dígitos: es la forma en que se compara el número contra lo que lee el OCR,
    /// sin depender de guiones, barras ni espacios. <c>null</c> si no hay ningún dígito — Postgres
    /// trata los NULL como distintos dentro del índice único, así que varias cuentas sin número
    /// conviven en la misma empresa.
    /// </summary>
    private static string? NormalizeNumber(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var digits = new string(value.Where(char.IsDigit).ToArray());
        return digits.Length == 0 ? null : digits;
    }

    /// <summary>
    /// Chequeo explícito de unicidad para devolver un 409 legible en vez de dejar que reviente el
    /// índice único con una excepción de base de datos. Dos cuentas SIN número no colisionan.
    /// </summary>
    private static async Task<bool> NumberAlreadyUsedAsync(
        ContableAIDbContext db, Guid companyId, string? normalized, Guid? excludingId)
    {
        if (normalized is null) return false;

        return await db.BankAccounts.AsNoTracking()
            .AnyAsync(a => a.CompanyId == companyId
                        && a.NormalizedNumber == normalized
                        && (excludingId == null || a.Id != excludingId));
    }

    /// <summary>
    /// PUENTE TEMPORAL (fase 1.b → se elimina en 1.c).
    ///
    /// El generador de asientos todavía resuelve la contrapartida leyendo
    /// <c>Company.BankAccountName</c> / <c>UsdBankAccountName</c>. Como la ficha de empresa ya no
    /// expone esos campos —los reemplazó el ABM de cuentas—, hay que mantenerlos al día o el
    /// contador se quedaría sin forma de configurar la contrapartida hasta que la fase 1.c cambie
    /// el generador.
    ///
    /// Solo escribe cuando hay una cuenta activa de esa moneda con contrapartida cargada: nunca
    /// borra un valor legacy existente. Con varias cuentas de la misma moneda toma la primera por
    /// alias, que es todo lo que el modelo viejo puede representar.
    /// </summary>
    private static async Task SyncLegacyCompanyFieldsAsync(ContableAIDbContext db, Guid companyId)
    {
        var company = await db.Companies.FirstOrDefaultAsync(c => c.Id == companyId);
        if (company is null) return;

        var accounts = await db.BankAccounts.AsNoTracking()
            .Where(a => a.CompanyId == companyId && a.IsActive && a.ContraAccountName != string.Empty)
            .OrderBy(a => a.Alias)
            .ThenBy(a => a.Id)
            .ToListAsync();

        var ars = accounts.FirstOrDefault(a => a.Currency == Currencies.Ars);
        if (ars is not null) company.BankAccountName = ars.ContraAccountName;

        var usd = accounts.FirstOrDefault(a => a.Currency == Currencies.Usd);
        if (usd is not null) company.UsdBankAccountName = usd.ContraAccountName;

        await db.SaveChangesAsync();
    }

    private static BankAccountResponse ToResponse(BankAccount a) => new(
        a.Id, a.CompanyId, a.Alias, a.AccountNumber, a.NormalizedNumber, a.Cbu,
        a.BankCode, a.Currency, a.ContraAccountName, a.ChartOfAccountId, a.IsActive);
}

public sealed record BankAccountResponse(
    Guid    Id,
    Guid    CompanyId,
    string  Alias,
    string? AccountNumber,
    string? NormalizedNumber,
    string? Cbu,
    string? BankCode,
    string  Currency,
    string  ContraAccountName,
    Guid?   ChartOfAccountId,
    bool    IsActive);
