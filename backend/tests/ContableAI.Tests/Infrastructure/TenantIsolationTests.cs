using ContableAI.Domain.Entities;
using ContableAI.Domain.Enums;
using ContableAI.Infrastructure.Persistence;
using ContableAI.Infrastructure.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace ContableAI.Tests.Infrastructure;

/// <summary>
/// Tests anti-regresión del aislamiento multi-tenant (fix IDOR/BOLA C-1).
///
/// Verifican que los Global Query Filters del <see cref="ContableAIDbContext"/> impiden que
/// un estudio (StudioTenantId) acceda a empresas o transacciones de OTRO estudio manipulando
/// los IDs, y que las excepciones legítimas (SystemAdmin, contextos sin tenant, IgnoreQueryFilters)
/// siguen funcionando. Si alguien debilita el filtro, estos tests fallan — es intencional.
/// </summary>
public class TenantIsolationTests
{
    private const string StudioA = "studio-aaaa";
    private const string StudioB = "studio-bbbb";

    /// <summary>ICurrentTenantService configurable para simular distintos usuarios autenticados.</summary>
    private sealed class FakeTenant(string? tenantId, bool isSystemAdmin = false) : ICurrentTenantService
    {
        public string? StudioTenantId => tenantId;
        public bool IsAuthenticated   => tenantId is not null;
        public bool IsSystemAdmin     => isSystemAdmin;
    }

    private static DbContextOptions<ContableAIDbContext> OptionsFor(string dbName) =>
        new DbContextOptionsBuilder<ContableAIDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

    private static ContableAIDbContext CtxFor(string dbName, string? tenantId, bool isSystemAdmin = false)
    {
        // P-1 (pooling): el tenant ya no viaja por el constructor sino por SetTenant(),
        // igual que hace la registración scoped de producción al tomar la instancia del pool.
        var ctx = new ContableAIDbContext(OptionsFor(dbName));
        ctx.SetTenant(new FakeTenant(tenantId, isSystemAdmin));
        return ctx;
    }

    /// <summary>
    /// Siembra una empresa + una transacción por estudio usando un contexto SIN tenant
    /// (filtro desactivado). Devuelve los IDs de los recursos del estudio B (las "víctimas").
    /// </summary>
    private static (Guid bCompanyId, Guid bTxId, Guid aCompanyId, Guid aTxId) Seed(string dbName)
    {
        using var seedCtx = CtxFor(dbName, tenantId: null); // sin tenant → filtro OFF

        var companyA = new Company { Name = "Empresa A", Cuit = "20-1-9", StudioTenantId = StudioA };
        var companyB = new Company { Name = "Empresa B", Cuit = "20-2-8", StudioTenantId = StudioB };
        seedCtx.Companies.AddRange(companyA, companyB);

        // P-2: el filtro global ancla por la columna desnormalizada StudioTenantId (sin JOIN),
        // igual que la estampa el UploadBankStatementHandler al adoptar la transacción.
        var txA = new BankTransaction { Description = "Mov A", CompanyId = companyA.Id, TenantId = companyA.Id.ToString(), StudioTenantId = StudioA };
        var txB = new BankTransaction { Description = "Mov B", CompanyId = companyB.Id, TenantId = companyB.Id.ToString(), StudioTenantId = StudioB };
        seedCtx.BankTransactions.AddRange(txA, txB);

        seedCtx.SaveChanges();
        return (companyB.Id, txB.Id, companyA.Id, txA.Id);
    }

    [Fact]
    public void PooledContextReuse_SetTenantAlwaysOverwritesPreviousTenant()
    {
        // P-1: con pooling, la MISMA instancia de contexto sirve requests sucesivos de
        // distintos estudios. SetTenant se llama en cada lease y debe pisar el estado anterior:
        // si no lo hiciera, un request vería datos del tenant previo (fuga cross-tenant).
        var db = nameof(PooledContextReuse_SetTenantAlwaysOverwritesPreviousTenant);
        Seed(db);

        using var ctx = new ContableAIDbContext(OptionsFor(db));

        ctx.SetTenant(new FakeTenant(StudioA));
        ctx.Companies.AsNoTracking().Should().OnlyContain(c => c.StudioTenantId == StudioA);

        // "Devolución al pool" y re-lease por un request del estudio B.
        ctx.SetTenant(new FakeTenant(StudioB));
        ctx.Companies.AsNoTracking().Should().OnlyContain(c => c.StudioTenantId == StudioB);

        // Re-lease por un job sin tenant (Hangfire): filtro desactivado, ve todo.
        ctx.SetTenant(null);
        ctx.Companies.AsNoTracking().Should().HaveCount(2);
    }

    [Fact]
    public void Company_List_IsScopedToCurrentStudio()
    {
        var db = nameof(Company_List_IsScopedToCurrentStudio);
        Seed(db);

        using var ctxA = CtxFor(db, StudioA);
        var visible = ctxA.Companies.AsNoTracking().ToList();

        visible.Should().OnlyContain(c => c.StudioTenantId == StudioA);
        visible.Should().HaveCount(1);
    }

    [Fact]
    public void Company_GetById_FromForeignStudio_ReturnsNull()
    {
        var db = nameof(Company_GetById_FromForeignStudio_ReturnsNull);
        var (bCompanyId, _, aCompanyId, _) = Seed(db);

        using var ctxA = CtxFor(db, StudioA);

        // La empresa del estudio B es invisible para el estudio A → NotFound en el handler.
        ctxA.Companies.FirstOrDefault(c => c.Id == bCompanyId).Should().BeNull();
        // Sanity: la propia sí se ve.
        ctxA.Companies.FirstOrDefault(c => c.Id == aCompanyId).Should().NotBeNull();
    }

    [Fact]
    public void Transaction_GetById_FromForeignStudio_ReturnsNull()
    {
        var db = nameof(Transaction_GetById_FromForeignStudio_ReturnsNull);
        var (_, bTxId, _, aTxId) = Seed(db);

        using var ctxA = CtxFor(db, StudioA);

        ctxA.BankTransactions.FirstOrDefault(t => t.Id == bTxId).Should().BeNull();
        ctxA.BankTransactions.FirstOrDefault(t => t.Id == aTxId).Should().NotBeNull();
    }

    [Fact]
    public void Transaction_BulkByIds_ExcludesForeignRows()
    {
        var db = nameof(Transaction_BulkByIds_ExcludesForeignRows);
        var (_, bTxId, _, aTxId) = Seed(db);

        using var ctxA = CtxFor(db, StudioA);
        var ids = new List<Guid> { aTxId, bTxId };

        // Simula el PUT /api/transactions/bulk: aunque el atacante mande el ID ajeno, se descarta.
        var affected = ctxA.BankTransactions.Where(t => ids.Contains(t.Id)).ToList();

        affected.Should().ContainSingle().Which.Id.Should().Be(aTxId);
    }

    [Fact]
    public void SystemAdmin_BypassesFilter_AndSeesAllStudios()
    {
        var db = nameof(SystemAdmin_BypassesFilter_AndSeesAllStudios);
        var (bCompanyId, bTxId, _, _) = Seed(db);

        using var admin = CtxFor(db, tenantId: "system", isSystemAdmin: true);

        admin.Companies.Count().Should().Be(2);
        admin.BankTransactions.Count().Should().Be(2);
        admin.Companies.FirstOrDefault(c => c.Id == bCompanyId).Should().NotBeNull();
        admin.BankTransactions.FirstOrDefault(t => t.Id == bTxId).Should().NotBeNull();
    }

    [Fact]
    public void IgnoreQueryFilters_SeesAcrossStudios()
    {
        var db = nameof(IgnoreQueryFilters_SeesAcrossStudios);
        Seed(db);

        using var ctxA = CtxFor(db, StudioA);

        // Patrón usado por el chequeo global de unicidad de CUIT en CreateCompanyHandler.
        var totalAcrossStudios = ctxA.Companies.IgnoreQueryFilters().Count();
        totalAcrossStudios.Should().Be(2);

        // Sin IgnoreQueryFilters, el mismo contexto solo ve su estudio.
        ctxA.Companies.Count().Should().Be(1);
    }

    [Fact]
    public void NoTenantContext_HasFilterDisabled_ForSeedingAndJobs()
    {
        var db = nameof(NoTenantContext_HasFilterDisabled_ForSeedingAndJobs);
        Seed(db);

        // Contexto sin usuario autenticado (seed, background jobs, login): ve todo.
        using var jobCtx = CtxFor(db, tenantId: null);
        jobCtx.Companies.Count().Should().Be(2);
        jobCtx.BankTransactions.Count().Should().Be(2);
    }

    // ── AccountingRule ────────────────────────────────────────────────────────
    // Epic D: hasta el hardening, AccountingRule NO tenía filtro global y los endpoints de
    // /api/rules buscaban la regla solo por Id — con el Id de una regla ajena se podía leerla,
    // editarla, desactivarla o borrarla desde otro estudio.

    private sealed record SeededRules(Guid ACompanyRule, Guid AStudioRule, Guid BCompanyRule, Guid BStudioRule, Guid SystemRule);

    /// <summary>
    /// Siembra el juego completo de niveles de regla para ambos estudios. Toda regla lleva su
    /// estudio desnormalizado, incluidas las de empresa: es el ancla del filtro.
    /// </summary>
    private static SeededRules SeedRules(string dbName)
    {
        var (bCompanyId, _, aCompanyId, _) = Seed(dbName);

        using var seedCtx = CtxFor(dbName, tenantId: null); // sin tenant → filtro OFF

        var aCompanyRule = new AccountingRule { Keyword = "A-EMPRESA", TargetAccount = "X", CompanyId = aCompanyId, StudioTenantId = StudioA };
        var aStudioRule  = new AccountingRule { Keyword = "A-ESTUDIO", TargetAccount = "X", CompanyId = null,       StudioTenantId = StudioA };
        var bCompanyRule = new AccountingRule { Keyword = "B-EMPRESA", TargetAccount = "X", CompanyId = bCompanyId, StudioTenantId = StudioB };
        var bStudioRule  = new AccountingRule { Keyword = "B-ESTUDIO", TargetAccount = "X", CompanyId = null,       StudioTenantId = StudioB };
        var systemRule   = new AccountingRule { Keyword = "SISTEMA",   TargetAccount = "X", CompanyId = null,       StudioTenantId = null };

        seedCtx.AccountingRules.AddRange(aCompanyRule, aStudioRule, bCompanyRule, bStudioRule, systemRule);
        seedCtx.SaveChanges();

        return new SeededRules(aCompanyRule.Id, aStudioRule.Id, bCompanyRule.Id, bStudioRule.Id, systemRule.Id);
    }

    [Fact]
    public void Rule_List_IsScopedToCurrentStudio_PlusSystemRules()
    {
        var db = nameof(Rule_List_IsScopedToCurrentStudio_PlusSystemRules);
        SeedRules(db);

        using var ctxA = CtxFor(db, StudioA);

        ctxA.AccountingRules.AsNoTracking().Select(r => r.Keyword)
            .Should().BeEquivalentTo(["A-EMPRESA", "A-ESTUDIO", "SISTEMA"]);
    }

    [Fact]
    public void Rule_GetById_FromForeignStudio_ReturnsNull()
    {
        var db = nameof(Rule_GetById_FromForeignStudio_ReturnsNull);
        var ids = SeedRules(db);

        using var ctxA = CtxFor(db, StudioA);

        // El IDOR concreto: conocer el Id de una regla ajena no alcanza para tocarla.
        ctxA.AccountingRules.FirstOrDefault(r => r.Id == ids.BCompanyRule).Should().BeNull();
        ctxA.AccountingRules.FirstOrDefault(r => r.Id == ids.BStudioRule).Should().BeNull();

        // Sanity: las propias y las de sistema sí se ven.
        ctxA.AccountingRules.FirstOrDefault(r => r.Id == ids.ACompanyRule).Should().NotBeNull();
        ctxA.AccountingRules.FirstOrDefault(r => r.Id == ids.SystemRule).Should().NotBeNull();
    }

    [Fact]
    public void Rule_SystemRules_AreVisibleToEveryStudio()
    {
        var db = nameof(Rule_SystemRules_AreVisibleToEveryStudio);
        var ids = SeedRules(db);

        using var ctxA = CtxFor(db, StudioA);
        using var ctxB = CtxFor(db, StudioB);

        // La clasificación depende de que las reglas de sistema lleguen a todos los estudios.
        ctxA.AccountingRules.Any(r => r.Id == ids.SystemRule).Should().BeTrue();
        ctxB.AccountingRules.Any(r => r.Id == ids.SystemRule).Should().BeTrue();
    }

    [Fact]
    public void Rule_WithCompanyButNoStudio_IsInvisible_FailClosed()
    {
        var db = nameof(Rule_WithCompanyButNoStudio_IsInvisible_FailClosed);
        var (bCompanyId, _, _, _) = Seed(db);

        using (var seedCtx = CtxFor(db, tenantId: null))
        {
            // Fila anómala: regla de empresa sin estudio estampado (backfill incompleto, o una
            // ruta de escritura que se olvide de estamparlo). El filtro reconoce la regla de
            // sistema por su forma COMPLETA (sin empresa Y sin estudio), así que esta queda
            // invisible en vez de quedar expuesta a todos los estudios.
            seedCtx.AccountingRules.Add(new AccountingRule
            {
                Keyword = "HUERFANA", TargetAccount = "X", CompanyId = bCompanyId, StudioTenantId = null,
            });
            seedCtx.SaveChanges();
        }

        using var ctxA = CtxFor(db, StudioA);
        using var ctxB = CtxFor(db, StudioB);

        ctxA.AccountingRules.Any(r => r.Keyword == "HUERFANA").Should().BeFalse();
        ctxB.AccountingRules.Any(r => r.Keyword == "HUERFANA").Should().BeFalse();

        // Sigue siendo alcanzable para reparar el dato (purga, backfill, soporte).
        using var jobCtx = CtxFor(db, tenantId: null);
        jobCtx.AccountingRules.Any(r => r.Keyword == "HUERFANA").Should().BeTrue();
    }

    [Fact]
    public void Rule_SystemAdmin_BypassesFilter_AndSeesAllStudios()
    {
        var db = nameof(Rule_SystemAdmin_BypassesFilter_AndSeesAllStudios);
        SeedRules(db);

        using var admin = CtxFor(db, tenantId: "system", isSystemAdmin: true);

        // El ABM de reglas globales del panel de administración depende de este bypass.
        admin.AccountingRules.Count().Should().Be(5);
    }
}
