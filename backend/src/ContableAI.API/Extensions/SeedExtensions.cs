using ContableAI.Domain.Entities;
using ContableAI.Infrastructure.Persistence;
using ContableAI.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace ContableAI.API.Extensions;

public static class SeedExtensions
{
    /// <summary>
    /// Ejecuta migraciones pendientes y siembra datos iniciales (reglas globales + plan de cuentas).
    /// Usa upsert: añade solo lo que no existe, nunca borra datos existentes.
    /// </summary>
    public static async Task SeedDatabaseAsync(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ContableAIDbContext>();

        await db.Database.MigrateAsync();

        await SeedGlobalRulesAsync(db);
        await SeedChartOfAccountsAsync(db);
    }

    private static async Task SeedGlobalRulesAsync(ContableAIDbContext db)
    {
        // Upsert: insertar solo las reglas cuya combinación (Keyword, Direction) no existe aún.
        var existing = await db.AccountingRules
            .Where(r => r.CompanyId == null)
            .Select(r => r.Keyword + "|" + (r.Direction == null ? "null" : r.Direction.ToString()))
            .ToHashSetAsync();

        var toAdd = GlobalRules.GetDefaults()
            .Where(r =>
            {
                var key = r.Keyword + "|" + (r.Direction == null ? "null" : r.Direction.ToString());
                return !existing.Contains(key);
            })
            .Select(r => new AccountingRule
            {
                Keyword             = r.Keyword,
                Direction           = r.Direction,
                TargetAccount       = r.TargetAccount,
                Priority            = r.Priority,
                RequiresTaxMatching = r.RequiresTaxMatching,
                CompanyId           = null,
            })
            .ToList();

        if (toAdd.Count > 0)
        {
            db.AccountingRules.AddRange(toAdd);
            await db.SaveChangesAsync();
            Console.WriteLine($"[Seed] {toAdd.Count} nuevas reglas globales insertadas.");
        }
    }

    private static async Task SeedChartOfAccountsAsync(ContableAIDbContext db)
    {
        // Upsert: insertar solo las cuentas que no existen aún por nombre.
        var existing = await db.ChartOfAccounts
            .Where(a => a.StudioTenantId == null)
            .Select(a => a.Name)
            .ToHashSetAsync();

        var toAdd = GlobalRules.GetDefaultAccounts()
            .Where(name => !existing.Contains(name))
            .Select(name => new ChartOfAccount { Name = name, StudioTenantId = null })
            .ToList();

        if (toAdd.Count > 0)
        {
            db.ChartOfAccounts.AddRange(toAdd);
            await db.SaveChangesAsync();
            Console.WriteLine($"[Seed] {toAdd.Count} nuevas cuentas del plan de cuentas insertadas.");
        }
    }
}
