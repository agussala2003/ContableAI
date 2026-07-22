using ContableAI.Application.Features.Companies.Commands;
using ContableAI.Infrastructure.Features.Companies;
using ContableAI.Infrastructure.Persistence;
using ContableAI.Infrastructure.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace ContableAI.Tests.Infrastructure;

/// <summary>
/// Tests de la unicidad de CUIT POR ESTUDIO en CreateCompanyHandler (fix M-3).
/// Dos estudios distintos pueden gestionar el mismo contribuyente; dentro de un mismo
/// estudio, el CUIT duplicado se rechaza.
/// </summary>
public class CompanyCuitUniquenessTests
{
    private const string StudioA = "studio-aaaa";
    private const string StudioB = "studio-bbbb";
    private const string Cuit    = "20-12345678-9";

    /// <summary>Quota siempre disponible: aísla el test a la lógica de unicidad de CUIT.</summary>
    private sealed class AlwaysAllowQuota : IQuotaService
    {
        public Task<QuotaLimits> GetLimitsAsync(string studioTenantId) => throw new NotImplementedException();
        public Task<QuotaUsage>  GetUsageAsync(string studioTenantId)  => throw new NotImplementedException();
        public Task<bool> CanAddCompanyAsync(string studioTenantId)               => Task.FromResult(true);
        public Task<bool> CanAddRuleAsync(string studioTenantId, Guid companyId)  => Task.FromResult(true);
        public Task<bool> CanUploadTransactionsAsync(string studioTenantId, int count) => Task.FromResult(true);
    }

    // Contexto sin tenant (filtro global OFF): el handler valida por su predicado explícito.
    private static ContableAIDbContext NewDb(string dbName) =>
        new(new DbContextOptionsBuilder<ContableAIDbContext>().UseInMemoryDatabase(dbName).Options);

    private static CreateCompanyCommand Cmd(string studio) =>
        new("Contribuyente", Cuit, "GENERAL", "Banco - CC", studio);

    [Fact]
    public async Task SameCuit_InDifferentStudios_IsAllowed()
    {
        using var db = NewDb(nameof(SameCuit_InDifferentStudios_IsAllowed));
        var handler = new CreateCompanyHandler(db, new AlwaysAllowQuota());

        var first  = await handler.Handle(Cmd(StudioA), default);
        var second = await handler.Handle(Cmd(StudioB), default);

        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue("dos estudios distintos pueden gestionar el mismo CUIT");
        db.Companies.Count().Should().Be(2);
    }

    [Fact]
    public async Task DuplicateCuit_InSameStudio_IsRejected()
    {
        using var db = NewDb(nameof(DuplicateCuit_InSameStudio_IsRejected));
        var handler = new CreateCompanyHandler(db, new AlwaysAllowQuota());

        var first  = await handler.Handle(Cmd(StudioA), default);
        var second = await handler.Handle(Cmd(StudioA), default);

        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeFalse("el CUIT ya existe en el mismo estudio");
        second.StatusCode.Should().Be(409);
        db.Companies.Count().Should().Be(1);
    }
}
