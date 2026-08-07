using ContableAI.Domain.Constants;
using ContableAI.Domain.Entities;
using ContableAI.Domain.Enums;
using ContableAI.Infrastructure.Persistence;
using ContableAI.Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ContableAI.API.Extensions;

public static class SeedExtensions
{
    /// <summary>
    /// Ejecuta migraciones pendientes y siembra datos iniciales (reglas globales + plan de cuentas + admin).
    /// Usa upsert: añade solo lo que no existe, nunca borra datos existentes.
    /// </summary>
    public static async Task SeedDatabaseAsync(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var db      = scope.ServiceProvider.GetRequiredService<ContableAIDbContext>();
        var hasher  = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();
        var logger  = app.Logger;

        await db.Database.MigrateAsync();

        // Habilitar extensión unaccent para búsqueda sin distinción de tildes
        try { await db.Database.ExecuteSqlRawAsync("CREATE EXTENSION IF NOT EXISTS unaccent;"); }
        catch { /* Si no hay permisos o la extensión no está disponible, continuar */ }

        await SeedAdminUserAsync(db, hasher, app.Environment, logger);
        await SeedGlobalRulesAsync(db, logger);
        await SeedChartOfAccountsAsync(db, logger);
    }

    private const int MinAdminPasswordLength = 12;

    private static async Task SeedAdminUserAsync(
        ContableAIDbContext db, IPasswordHasher<User> hasher, IHostEnvironment env, ILogger logger)
    {
        const string adminEmail = "admin@contableai.com";
        if (await db.Users.AnyAsync(u => u.Email == adminEmail))
            return;

        // A-1: la contraseña del admin se toma de la env var SEED_ADMIN_PASSWORD.
        // Fuera de Development es OBLIGATORIA: si falta, el arranque falla (no hay password
        // por defecto débil en producción). En Development se admite un fallback local.
        var adminPassword = Environment.GetEnvironmentVariable("SEED_ADMIN_PASSWORD");

        if (string.IsNullOrWhiteSpace(adminPassword))
        {
            if (!env.IsDevelopment())
                throw new InvalidOperationException(
                    "SEED_ADMIN_PASSWORD es obligatoria fuera de Development. Definí una contraseña " +
                    $"fuerte (mínimo {MinAdminPasswordLength} caracteres) en las variables de entorno " +
                    "del servidor antes de arrancar. No existe contraseña por defecto en producción.");

            adminPassword = "Admin123!";
            logger.LogWarning(
                "[Seed] SEED_ADMIN_PASSWORD no definida — usando contraseña de DESARROLLO para {Email}. " +
                "Nunca uses este fallback en producción.", adminEmail);
        }
        else if (adminPassword.Length < MinAdminPasswordLength)
        {
            throw new InvalidOperationException(
                $"SEED_ADMIN_PASSWORD debe tener al menos {MinAdminPasswordLength} caracteres.");
        }

        var admin = new User
        {
            Email          = adminEmail,
            DisplayName    = "Admin",
            StudioTenantId = TenantConstants.System,
            Role           = UserRole.SystemAdmin,
            AccountStatus  = AccountStatus.Active,
            IsActive       = true,
            Plan           = StudioPlan.Enterprise,
        };
        admin.PasswordHash = hasher.HashPassword(admin, adminPassword);

        db.Users.Add(admin);
        await db.SaveChangesAsync();
        logger.LogInformation("[Seed] Usuario admin creado: {Email}", adminEmail);
    }

    private static async Task SeedGlobalRulesAsync(ContableAIDbContext db, ILogger logger)
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
            logger.LogInformation("[Seed] {Count} nuevas reglas globales insertadas.", toAdd.Count);
        }

        await RepointBridgeRulesAsync(db, logger);
    }

    /// <summary>
    /// Repunta a la cuenta puente las reglas de sistema de transferencias entre cuentas propias que
    /// quedaron apuntando a otra cuenta (históricamente "CAJA Y BANCOS", que además se usa como
    /// cuenta genérica de disponibilidades y por eso contaminaba el neteo).
    ///
    /// Hace falta porque el upsert de arriba es solo-inserción: una regla ya existente conserva su
    /// <c>TargetAccount</c> para siempre, así que cambiar el valor en <see cref="GlobalRules"/> no
    /// alcanza para las bases que ya tienen la regla sembrada.
    ///
    /// Es idempotente y acotado: solo toca reglas de SISTEMA (sin empresa ni estudio) cuyo keyword
    /// esté en <see cref="GlobalRules.BridgeKeywords"/> y que todavía no apunten al puente — nunca
    /// pisa reglas de empresa/estudio ni ediciones hechas por un admin sobre otras reglas.
    /// </summary>
    private static async Task RepointBridgeRulesAsync(ContableAIDbContext db, ILogger logger)
    {
        var bridgeKeywords = GlobalRules.BridgeKeywords.ToList();

        // También se corrige la prioridad: sin eso, "MISMA TITULARIDAD" seguiría empatada en 12 con
        // la regla "TRANSFER" → TARJETAS DE CREDITO y el desempate quedaría librado al orden de la
        // lista. Al ir a 9, una transferencia entre cuentas propias le gana siempre.
        // La condición sobre TargetAccount hace que esto corra una sola vez por regla: una vez
        // repuntada, un ajuste posterior del admin no se vuelve a pisar.
        var repointed = await db.AccountingRules
            .Where(r => r.CompanyId == null
                     && r.StudioTenantId == null
                     && bridgeKeywords.Contains(r.Keyword)
                     && r.TargetAccount != GlobalRules.BridgeAccount)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.TargetAccount, GlobalRules.BridgeAccount)
                .SetProperty(r => r.Priority,      GlobalRules.BridgePriority));

        if (repointed > 0)
            logger.LogInformation(
                "[Seed] {Count} regla(s) de transferencias entre cuentas propias repuntadas a '{Account}'.",
                repointed, GlobalRules.BridgeAccount);
    }

    private static async Task SeedChartOfAccountsAsync(ContableAIDbContext db, ILogger logger)
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
            logger.LogInformation("[Seed] {Count} nuevas cuentas del plan de cuentas insertadas.", toAdd.Count);
        }
    }
}
