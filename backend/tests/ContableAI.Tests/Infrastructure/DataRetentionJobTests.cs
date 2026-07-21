using ContableAI.Domain.Entities;
using ContableAI.Infrastructure.BackgroundJobs;
using ContableAI.Infrastructure.Options;
using ContableAI.Infrastructure.Persistence;
using ContableAI.Infrastructure.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ContableAI.Tests.Infrastructure;

/// <summary>
/// Tests del job diario de retención (P-4/P-5 + purga diferida de P-2): debe purgar solo lo
/// vencido según las ventanas configuradas y no tocar nada dentro de ventana.
/// </summary>
public class DataRetentionJobTests
{
    private sealed class NoTenant : ICurrentTenantService
    {
        public string? StudioTenantId => null; // como en Hangfire: sin HttpContext → filtro OFF
        public bool IsAuthenticated   => false;
        public bool IsSystemAdmin     => false;
    }

    private static ContableAIDbContext CtxFor(string dbName) =>
        new(new DbContextOptionsBuilder<ContableAIDbContext>()
                .UseInMemoryDatabase(dbName)
                .Options,
            new NoTenant());

    private static DataRetentionJob JobFor(ContableAIDbContext ctx, DataRetentionOptions? options = null) =>
        new(ctx, Microsoft.Extensions.Options.Options.Create(options ?? new DataRetentionOptions()),
            NullLogger<DataRetentionJob>.Instance);

    [Fact]
    public async Task Run_PurgesExpiredUploadJobResults_KeepsRecent()
    {
        var db = nameof(Run_PurgesExpiredUploadJobResults_KeepsRecent);
        using (var seed = CtxFor(db))
        {
            seed.UploadJobResults.Add(new UploadJobResult
                { JobId = "viejo", StudioTenantId = "t1", ResultJson = "{}", CreatedAt = DateTime.UtcNow.AddDays(-31) });
            seed.UploadJobResults.Add(new UploadJobResult
                { JobId = "reciente", StudioTenantId = "t1", ResultJson = "{}", CreatedAt = DateTime.UtcNow.AddDays(-29) });
            seed.SaveChanges();
        }

        using (var ctx = CtxFor(db))
            await JobFor(ctx).RunAsync();

        using var check = CtxFor(db);
        var remaining = await check.UploadJobResults.Select(r => r.JobId).ToListAsync();
        remaining.Should().Equal("reciente");
    }

    [Fact]
    public async Task Run_PurgesOrphanStagedFiles_KeepsInFlight()
    {
        var db = nameof(Run_PurgesOrphanStagedFiles_KeepsInFlight);
        using (var seed = CtxFor(db))
        {
            seed.StagedUploadFiles.Add(new StagedUploadFile
                { FileName = "huerfano.pdf", CreatedAt = DateTime.UtcNow.AddHours(-25) });
            seed.StagedUploadFiles.Add(new StagedUploadFile
                { FileName = "en-vuelo.pdf", CreatedAt = DateTime.UtcNow.AddHours(-1) });
            seed.SaveChanges();
        }

        using (var ctx = CtxFor(db))
            await JobFor(ctx).RunAsync();

        using var check = CtxFor(db);
        var remaining = await check.StagedUploadFiles.Select(f => f.FileName).ToListAsync();
        remaining.Should().Equal("en-vuelo.pdf");
    }

    [Fact]
    public async Task Run_HardDeletesCompaniesPastRetentionWindow_WithFullCascade()
    {
        var db = nameof(Run_HardDeletesCompaniesPastRetentionWindow_WithFullCascade);
        Guid expiredId, graceId;
        using (var seed = CtxFor(db))
        {
            // Empresa dada de baja hace 100 días → debe purgarse con todo su historial.
            var expired = new Company
            {
                Name = "Vencida SRL", Cuit = "30-1-1", StudioTenantId = "studio-ret",
                IsActive = false, DeletedAt = DateTime.UtcNow.AddDays(-100),
            };
            // Empresa dada de baja hace 10 días → dentro de la ventana de gracia, intacta.
            var grace = new Company
            {
                Name = "En Gracia SA", Cuit = "30-2-2", StudioTenantId = "studio-ret",
                IsActive = false, DeletedAt = DateTime.UtcNow.AddDays(-10),
            };
            seed.Companies.AddRange(expired, grace);
            expiredId = expired.Id;
            graceId   = grace.Id;

            foreach (var (companyId, tag) in new[] { (expiredId, "V"), (graceId, "G") })
            {
                var tx = new BankTransaction { Description = $"MOV {tag}", CompanyId = companyId, TenantId = "studio-ret" };
                seed.BankTransactions.Add(tx);
                var je = new JournalEntry { CompanyId = companyId, BankTransactionId = tx.Id, Description = $"Asiento {tag}" };
                seed.JournalEntries.Add(je);
                seed.JournalEntryLines.Add(new JournalEntryLine { JournalEntryId = je.Id, Account = "Caja", Amount = 10m, IsDebit = true });
                seed.AccountingRules.Add(new AccountingRule { Keyword = $"KW-{tag}", TargetAccount = "Caja", CompanyId = companyId });
                seed.RuleSuggestions.Add(new RuleSuggestion { TenantId = "studio-ret", CompanyId = companyId, SuggestedAccount = "Caja", Frequency = 3 });
                seed.AfipVouchers.Add(new AfipVoucher { CompanyId = companyId, TenantId = "studio-ret", Date = new DateOnly(2026, 2, 1), Amount = 5m, TaxName = $"IVA {tag}" });
            }
            seed.SaveChanges();
        }

        using (var ctx = CtxFor(db))
            await JobFor(ctx).RunAsync();

        using var check = CtxFor(db);

        // La vencida desapareció con toda su cascada.
        (await check.Companies.AnyAsync(c => c.Id == expiredId)).Should().BeFalse();
        (await check.BankTransactions.IgnoreQueryFilters().AnyAsync(t => t.CompanyId == expiredId)).Should().BeFalse();
        (await check.JournalEntries.AnyAsync(j => j.CompanyId == expiredId)).Should().BeFalse();
        (await check.AccountingRules.AnyAsync(r => r.CompanyId == expiredId)).Should().BeFalse();
        (await check.RuleSuggestions.AnyAsync(s => s.CompanyId == expiredId)).Should().BeFalse();
        (await check.AfipVouchers.AnyAsync(v => v.CompanyId == expiredId)).Should().BeFalse();
        (await check.JournalEntryLines.CountAsync()).Should().Be(1, "solo la línea de la empresa en gracia");

        // La que está dentro de la ventana de 90 días sigue completa.
        (await check.Companies.AnyAsync(c => c.Id == graceId)).Should().BeTrue();
        (await check.BankTransactions.IgnoreQueryFilters().AnyAsync(t => t.CompanyId == graceId)).Should().BeTrue();
        (await check.JournalEntries.AnyAsync(j => j.CompanyId == graceId)).Should().BeTrue();
    }

    [Fact]
    public async Task Run_RespectsConfigurableWindows()
    {
        var db = nameof(Run_RespectsConfigurableWindows);
        using (var seed = CtxFor(db))
        {
            seed.UploadJobResults.Add(new UploadJobResult
                { JobId = "r7", StudioTenantId = "t1", ResultJson = "{}", CreatedAt = DateTime.UtcNow.AddDays(-8) });
            seed.SaveChanges();
        }

        // Con la ventana default (30 días) no se purgaría; con 7 días, sí.
        using (var ctx = CtxFor(db))
            await JobFor(ctx, new DataRetentionOptions { UploadJobResultsDays = 7 }).RunAsync();

        using var check = CtxFor(db);
        (await check.UploadJobResults.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Run_WithNothingExpired_IsNoOp()
    {
        var db = nameof(Run_WithNothingExpired_IsNoOp);
        using (var seed = CtxFor(db))
        {
            seed.UploadJobResults.Add(new UploadJobResult
                { JobId = "fresco", StudioTenantId = "t1", ResultJson = "{}", CreatedAt = DateTime.UtcNow });
            seed.StagedUploadFiles.Add(new StagedUploadFile { FileName = "fresco.pdf", CreatedAt = DateTime.UtcNow });
            seed.Companies.Add(new Company { Name = "Activa", Cuit = "30-3-3", StudioTenantId = "s", IsActive = true });
            seed.SaveChanges();
        }

        using (var ctx = CtxFor(db))
            await JobFor(ctx).RunAsync();

        using var check = CtxFor(db);
        (await check.UploadJobResults.CountAsync()).Should().Be(1);
        (await check.StagedUploadFiles.CountAsync()).Should().Be(1);
        (await check.Companies.CountAsync()).Should().Be(1);
    }
}
