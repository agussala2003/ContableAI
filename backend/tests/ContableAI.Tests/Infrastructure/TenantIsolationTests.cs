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

    private static ContableAIDbContext CtxFor(string dbName, string? tenantId, bool isSystemAdmin = false) =>
        new(OptionsFor(dbName), new FakeTenant(tenantId, isSystemAdmin));

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

        var txA = new BankTransaction { Description = "Mov A", CompanyId = companyA.Id, TenantId = companyA.Id.ToString() };
        var txB = new BankTransaction { Description = "Mov B", CompanyId = companyB.Id, TenantId = companyB.Id.ToString() };
        seedCtx.BankTransactions.AddRange(txA, txB);

        seedCtx.SaveChanges();
        return (companyB.Id, txB.Id, companyA.Id, txA.Id);
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
}
