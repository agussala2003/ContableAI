using ContableAI.Application.Features.Admin.Commands;
using ContableAI.Domain.Entities;
using ContableAI.Domain.Enums;
using ContableAI.Infrastructure.Features.Admin;
using ContableAI.Infrastructure.Persistence;
using ContableAI.Infrastructure.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContableAI.Tests.Infrastructure;

/// <summary>
/// Tests de integración (EF InMemory) del cierre formal de cuenta (P-1/P-3):
/// la cascada debe vaciar TODAS las tablas del tenant sin tocar a los demás estudios,
/// y los AuditLogs deben seudonimizarse (no borrarse).
/// </summary>
public class DeleteStudioTenantHandlerTests
{
    private sealed class FakeTenant(string? tenantId, bool isSystemAdmin = false) : ICurrentTenantService
    {
        public string? StudioTenantId => tenantId;
        public bool IsAuthenticated   => tenantId is not null;
        public bool IsSystemAdmin     => isSystemAdmin;
    }

    private static ContableAIDbContext CtxFor(string dbName, string? tenantId = null) =>
        new(new DbContextOptionsBuilder<ContableAIDbContext>()
                .UseInMemoryDatabase(dbName)
                .Options,
            new FakeTenant(tenantId));

    private static DeleteStudioTenantHandler HandlerFor(ContableAIDbContext ctx) =>
        new(ctx, NullLogger<DeleteStudioTenantHandler>.Instance);

    /// <summary>
    /// Siembra un tenant completo (usuario, sesión, empresa, transacción, asiento con línea,
    /// reglas de empresa y de estudio, sugerencia, voucher AFIP, cuenta propia, período cerrado,
    /// resultado de job y auditoría) y devuelve su StudioTenantId (Guid string).
    /// </summary>
    private static (string tenantId, Guid userId, Guid companyId) SeedTenant(
        ContableAIDbContext ctx, string emailPrefix, string cuit)
    {
        var tenantId = Guid.NewGuid().ToString();

        var user = new User
        {
            Email          = $"{emailPrefix}@estudio.com",
            DisplayName    = emailPrefix,
            Role           = UserRole.StudioOwner,
            StudioTenantId = tenantId,
        };
        ctx.Users.Add(user);

        ctx.RefreshTokens.Add(new RefreshToken
        {
            UserId    = user.Id,
            TokenHash = $"hash-{Guid.NewGuid():N}",
            ExpiresAt = DateTime.UtcNow.AddDays(7),
        });

        var company = new Company { Name = $"Empresa {emailPrefix}", Cuit = cuit, StudioTenantId = tenantId };
        ctx.Companies.Add(company);

        var tx = new BankTransaction { Description = "PAGO PROVEEDOR", CompanyId = company.Id, TenantId = tenantId };
        ctx.BankTransactions.Add(tx);

        var je = new JournalEntry { CompanyId = company.Id, BankTransactionId = tx.Id, Description = "Asiento" };
        ctx.JournalEntries.Add(je);
        ctx.JournalEntryLines.Add(new JournalEntryLine { JournalEntryId = je.Id, Account = "Proveedores", Amount = 100m, IsDebit = true });

        ctx.AccountingRules.Add(new AccountingRule { Keyword = "PROVEEDOR", TargetAccount = "Proveedores", CompanyId = company.Id });
        ctx.AccountingRules.Add(new AccountingRule { Keyword = "BANCO", TargetAccount = "Bancos", CompanyId = null, StudioTenantId = Guid.Parse(tenantId) });

        ctx.RuleSuggestions.Add(new RuleSuggestion { TenantId = tenantId, CompanyId = company.Id, SuggestedAccount = "Proveedores", Frequency = 3 });
        ctx.AfipVouchers.Add(new AfipVoucher { CompanyId = company.Id, TenantId = tenantId, Date = new DateOnly(2026, 1, 15), Amount = 500m, TaxName = "IVA A Pagar" });
        ctx.ChartOfAccounts.Add(new ChartOfAccount { Name = $"Banco Propio {emailPrefix}", StudioTenantId = Guid.Parse(tenantId) });
        ctx.ClosedPeriods.Add(new ClosedPeriod { StudioTenantId = tenantId, Year = 2026, Month = 1, ClosedByEmail = user.Email });
        ctx.UploadJobResults.Add(new UploadJobResult { JobId = Guid.NewGuid().ToString(), StudioTenantId = tenantId, IsSuccess = true, ResultJson = "{}" });

        ctx.AuditLogs.Add(new AuditLog
        {
            TenantId  = tenantId,
            UserId    = user.Id.ToString(),
            UserEmail = user.Email,
            Action    = "Created",
            EntityName = "BankTransaction",
            EntityId  = tx.Id.ToString(),
            Changes   = """{"Description":"PAGO PROVEEDOR","Amount":100}""",
        });

        ctx.SaveChanges();
        return (tenantId, user.Id, company.Id);
    }

    [Fact]
    public async Task Handle_DeletesAllTenantData_AndKeepsOtherTenantIntact()
    {
        var db = nameof(Handle_DeletesAllTenantData_AndKeepsOtherTenantIntact);
        string tenantA, tenantB;
        using (var seed = CtxFor(db))
        {
            (tenantA, _, _) = SeedTenant(seed, "cerrado", "30-11111111-1");
            (tenantB, _, _) = SeedTenant(seed, "vivo", "30-22222222-2");
        }

        using var ctx = CtxFor(db);
        var result = await HandlerFor(ctx).Handle(
            new DeleteStudioTenantCommand(tenantA, "admin@contableai.com", "Pedido formal del titular"), default);

        result.IsSuccess.Should().BeTrue();
        result.Value!.UsersDeleted.Should().Be(1);
        result.Value.CompaniesDeleted.Should().Be(1);
        result.Value.BankTransactionsDeleted.Should().Be(1);
        result.Value.JournalEntriesDeleted.Should().Be(1);
        result.Value.JournalEntryLinesDeleted.Should().Be(1);
        result.Value.AccountingRulesDeleted.Should().Be(2); // regla de empresa + regla de estudio
        result.Value.RefreshTokensDeleted.Should().Be(1);
        result.Value.UploadJobResultsDeleted.Should().Be(1);

        using var check = CtxFor(db);

        // Tenant A: sin residuos en ninguna tabla (P-1/P-3).
        (await check.Users.CountAsync(u => u.StudioTenantId == tenantA)).Should().Be(0);
        (await check.Companies.CountAsync(c => c.StudioTenantId == tenantA)).Should().Be(0);
        (await check.BankTransactions.CountAsync(t => t.TenantId == tenantA)).Should().Be(0);
        (await check.JournalEntries.CountAsync()).Should().Be(1);      // solo el de B
        (await check.JournalEntryLines.CountAsync()).Should().Be(1);   // solo la de B
        (await check.AccountingRules.CountAsync(r => r.StudioTenantId == Guid.Parse(tenantA))).Should().Be(0);
        (await check.RuleSuggestions.CountAsync(s => s.TenantId == tenantA)).Should().Be(0);
        (await check.ChartOfAccounts.CountAsync(a => a.StudioTenantId == Guid.Parse(tenantA))).Should().Be(0);
        (await check.ClosedPeriods.CountAsync(p => p.StudioTenantId == tenantA)).Should().Be(0);
        (await check.UploadJobResults.CountAsync(r => r.StudioTenantId == tenantA)).Should().Be(0);
        (await check.RefreshTokens.CountAsync()).Should().Be(1);       // solo la sesión de B

        // Tenant B: intacto.
        (await check.Users.CountAsync(u => u.StudioTenantId == tenantB)).Should().Be(1);
        (await check.Companies.CountAsync(c => c.StudioTenantId == tenantB)).Should().Be(1);
        (await check.AccountingRules.CountAsync(r => r.StudioTenantId == Guid.Parse(tenantB))).Should().Be(1);
        (await check.AuditLogs.CountAsync(a => a.TenantId == tenantB && a.UserEmail == "vivo@estudio.com"))
            .Should().Be(1, "la auditoría de otros tenants no debe anonimizarse");
    }

    [Fact]
    public async Task Handle_AnonymizesAuditLogs_AndRecordsClosure()
    {
        var db = nameof(Handle_AnonymizesAuditLogs_AndRecordsClosure);
        string tenantId; Guid userId;
        using (var seed = CtxFor(db))
            (tenantId, userId, _) = SeedTenant(seed, "olvidado", "30-33333333-3");

        using var ctx = CtxFor(db);
        var result = await HandlerFor(ctx).Handle(
            new DeleteStudioTenantCommand(tenantId, "admin@contableai.com", "Derecho al olvido"), default);

        result.IsSuccess.Should().BeTrue();
        result.Value!.AuditLogsAnonymized.Should().Be(1);

        using var check = CtxFor(db);
        var logs = await check.AuditLogs.Where(a => a.TenantId == tenantId).ToListAsync();

        // La fila de auditoría original sobrevive (trazabilidad legal, P-6) pero seudonimizada.
        var original = logs.Single(l => l.EntityName == "BankTransaction");
        original.UserEmail.Should().Be($"deleted-user-{userId}@anonymized.local");
        original.Changes.Should().BeNull("el diff contiene datos financieros sensibles");

        // Y queda el registro del cierre: quién lo pidió y el motivo.
        var closure = logs.Single(l => l.EntityName == "StudioTenant");
        closure.UserEmail.Should().Be("admin@contableai.com");
        closure.Action.Should().Be("Deleted");
        closure.Changes.Should().Contain("Derecho al olvido");
    }

    [Fact]
    public async Task Handle_PurgesOrphanStagedFiles_ButKeepsRecentOnes()
    {
        var db = nameof(Handle_PurgesOrphanStagedFiles_ButKeepsRecentOnes);
        string tenantId;
        using (var seed = CtxFor(db))
        {
            (tenantId, _, _) = SeedTenant(seed, "staged", "30-44444444-4");
            seed.StagedUploadFiles.Add(new StagedUploadFile
                { FileName = "huerfano.pdf", CreatedAt = DateTime.UtcNow.AddDays(-3) });
            seed.StagedUploadFiles.Add(new StagedUploadFile
                { FileName = "en-vuelo.pdf", CreatedAt = DateTime.UtcNow.AddMinutes(-5) });
            seed.SaveChanges();
        }

        using var ctx = CtxFor(db);
        var result = await HandlerFor(ctx).Handle(
            new DeleteStudioTenantCommand(tenantId, "admin@contableai.com"), default);

        result.Value!.StagedFilesPurged.Should().Be(1);

        using var check = CtxFor(db);
        var remaining = await check.StagedUploadFiles.ToListAsync();
        remaining.Should().ContainSingle(f => f.FileName == "en-vuelo.pdf",
            "un archivo staged reciente puede tener un job de Hangfire en vuelo");
    }

    [Fact]
    public async Task Handle_UnknownTenant_ReturnsNotFound()
    {
        using var ctx = CtxFor(nameof(Handle_UnknownTenant_ReturnsNotFound));
        var result = await HandlerFor(ctx).Handle(
            new DeleteStudioTenantCommand(Guid.NewGuid().ToString(), "admin@contableai.com"), default);

        result.IsSuccess.Should().BeFalse();
        result.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task Handle_TenantWithSystemAdmin_IsRejected()
    {
        var db = nameof(Handle_TenantWithSystemAdmin_IsRejected);
        var tenantId = Guid.NewGuid().ToString();
        using (var seed = CtxFor(db))
        {
            seed.Users.Add(new User
            {
                Email          = "root@contableai.com",
                Role           = UserRole.SystemAdmin,
                StudioTenantId = tenantId,
            });
            seed.SaveChanges();
        }

        using var ctx = CtxFor(db);
        var result = await HandlerFor(ctx).Handle(
            new DeleteStudioTenantCommand(tenantId, "admin@contableai.com"), default);

        result.IsSuccess.Should().BeFalse();
        result.StatusCode.Should().Be(400);

        using var check = CtxFor(db);
        (await check.Users.CountAsync()).Should().Be(1, "no debe borrar nada");
    }
}
