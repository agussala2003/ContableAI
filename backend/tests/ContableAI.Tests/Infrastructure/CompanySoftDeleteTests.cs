using ContableAI.Application.Features.Companies.Commands;
using ContableAI.Application.Features.Companies.Queries;
using ContableAI.Domain.Entities;
using ContableAI.Infrastructure.Features.Companies;
using ContableAI.Infrastructure.Persistence;
using ContableAI.Infrastructure.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace ContableAI.Tests.Infrastructure;

/// <summary>
/// Tests del soft-delete seguro de Company (P-2): la baja debe estampar <c>DeletedAt</c>
/// (inicio de la ventana de purga diferida) y las empresas dadas de baja no deben ser
/// devueltas ni por el listado ni por la consulta por ID.
/// </summary>
public class CompanySoftDeleteTests
{
    private const string Studio = "studio-p2";

    private sealed class FakeTenant(string? tenantId) : ICurrentTenantService
    {
        public string? StudioTenantId => tenantId;
        public bool IsAuthenticated   => tenantId is not null;
        public bool IsSystemAdmin     => false;
    }

    private static ContableAIDbContext CtxFor(string dbName, string? tenantId = Studio) =>
        new(new DbContextOptionsBuilder<ContableAIDbContext>()
                .UseInMemoryDatabase(dbName)
                .Options,
            new FakeTenant(tenantId));

    private static Guid SeedCompany(string dbName)
    {
        using var seed = CtxFor(dbName, tenantId: null);
        var company = new Company { Name = "Baja SRL", Cuit = "30-99999999-9", StudioTenantId = Studio };
        seed.Companies.Add(company);
        seed.SaveChanges();
        return company.Id;
    }

    [Fact]
    public async Task Delete_SetsDeletedAtTimestamp_AndIsIdempotent()
    {
        var db = nameof(Delete_SetsDeletedAtTimestamp_AndIsIdempotent);
        var companyId = SeedCompany(db);

        using (var ctx = CtxFor(db))
        {
            var result = await new DeleteCompanyHandler(ctx).Handle(new DeleteCompanyCommand(companyId), default);
            result.IsSuccess.Should().BeTrue();
        }

        DateTime firstDeletedAt;
        using (var check = CtxFor(db, tenantId: null))
        {
            var company = await check.Companies.SingleAsync(c => c.Id == companyId);
            company.IsActive.Should().BeFalse();
            company.DeletedAt.Should().NotBeNull("DeletedAt marca el inicio de la ventana de purga de 90 días");
            company.DeletedAt!.Value.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
            firstDeletedAt = company.DeletedAt.Value;
        }

        // Un segundo DELETE no debe correr la ventana de retención.
        using (var ctx = CtxFor(db))
            await new DeleteCompanyHandler(ctx).Handle(new DeleteCompanyCommand(companyId), default);

        using (var check = CtxFor(db, tenantId: null))
            (await check.Companies.SingleAsync(c => c.Id == companyId))
                .DeletedAt.Should().Be(firstDeletedAt);
    }

    [Fact]
    public async Task GetById_AfterSoftDelete_ReturnsNotFound()
    {
        var db = nameof(GetById_AfterSoftDelete_ReturnsNotFound);
        var companyId = SeedCompany(db);

        using (var ctx = CtxFor(db))
            await new DeleteCompanyHandler(ctx).Handle(new DeleteCompanyCommand(companyId), default);

        using var queryCtx = CtxFor(db);
        var result = await new GetCompanyHandler(queryCtx).Handle(new GetCompanyQuery(companyId), default);

        result.IsSuccess.Should().BeFalse("una empresa dada de baja no existe para la API (P-2)");
        result.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task List_AfterSoftDelete_ExcludesCompany()
    {
        var db = nameof(List_AfterSoftDelete_ExcludesCompany);
        var companyId = SeedCompany(db);

        using (var ctx = CtxFor(db))
            await new DeleteCompanyHandler(ctx).Handle(new DeleteCompanyCommand(companyId), default);

        using var queryCtx = CtxFor(db);
        var result = await new GetCompaniesHandler(queryCtx).Handle(new GetCompaniesQuery(Studio), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }
}
