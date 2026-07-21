using ContableAI.Application.Common;
using ContableAI.Application.Features.Transactions.Commands;
using ContableAI.Domain.Constants;
using ContableAI.Domain.Entities;
using ContableAI.Domain.Enums;
using ContableAI.Infrastructure.Features.Transactions;
using ContableAI.Infrastructure.Persistence;
using ContableAI.Infrastructure.Services;
using ContableAI.Infrastructure.Services.Classification;
using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContableAI.Tests.Infrastructure;

/// <summary>
/// Tests de integración del orquestador de subida de extractos (<see cref="UploadBankStatementHandler"/>)
/// con EF Core InMemory. Ejercitan el flujo real: parseo (fake) → validación → cuota → deduplicación
/// (vía <c>TransactionSignatureBuilder</c>) → clasificación por reglas (motor real) → persistencia.
///
/// El handler corre como si fuera un job de Hangfire: no depende de <see cref="ICurrentTenantService"/>
/// (no hay HttpContext dentro de un job), sino que el tenant viaja explícito en el propio
/// <see cref="UploadBankStatementCommand"/>. El contenido del archivo se resuelve desde
/// <c>StagedUploadFiles</c> (staging bytea), igual que en producción — por eso cada test siembra
/// una fila de staging antes de invocar <c>Handle</c>.
/// </summary>
public class UploadBankStatementHandlerTests
{
    private const string Studio = "studio-upload-test";

    // ── Fakes de dependencias externas ─────────────────────────────────────────

    private sealed class FakeBankParser(Func<IEnumerable<BankTransaction>> factory) : IBankParserService
    {
        // Ignora el stream y devuelve movimientos frescos (el parseo real no es lo que se testea aquí).
        public IEnumerable<BankTransaction> Parse(Stream fileStream, string bankCode, string fileName) => factory();
        public IEnumerable<BankTransaction> ParseCsv(Stream fileStream, string bankCode) => throw new NotSupportedException();
    }

    private sealed class FakeQuota(bool canUpload) : IQuotaService
    {
        public Task<bool> CanUploadTransactionsAsync(string studioTenantId, int count) => Task.FromResult(canUpload);
        public Task<QuotaLimits> GetLimitsAsync(string studioTenantId) => throw new NotSupportedException();
        public Task<QuotaUsage>  GetUsageAsync(string studioTenantId)  => throw new NotSupportedException();
        public Task<bool> CanAddCompanyAsync(string studioTenantId)          => throw new NotSupportedException();
        public Task<bool> CanAddRuleAsync(string studioTenantId, Guid companyId) => throw new NotSupportedException();
    }

    private sealed class FakeJobClient : IBackgroundJobClient
    {
        public string Create(Job job, IState state) => Guid.NewGuid().ToString();
        public bool ChangeState(string jobId, IState state, string expectedState) => true;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static ContableAIDbContext NewDb(string dbName) =>
        new(new DbContextOptionsBuilder<ContableAIDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options); // tenant = null → filtro multi-tenant OFF (irrelevante: el handler ya no lo usa)

    private static UploadBankStatementHandler NewHandler(
        ContableAIDbContext db, Func<IEnumerable<BankTransaction>> parsed, bool canUpload = true)
        => new(
            db,
            new FakeBankParser(parsed),
            new ClassificationService(new HardRuleStrategy()), // motor de reglas real
            new FakeQuota(canUpload),
            new FakeJobClient(),
            NullLogger<UploadBankStatementHandler>.Instance);

    private static Company NewCompany() => new()
    {
        Name            = "Empresa Upload SRL",
        StudioTenantId  = Studio,
        IsActive        = true,
        BankAccountName = "Banco Test",
    };

    /// <summary>Siembra un archivo en staging (bytea) y devuelve la referencia que viaja en el command.</summary>
    private static async Task<StagedFileRef> StageCsvAsync(string dbName, string fileName = "extracto.csv")
    {
        var content = "fake-csv-content"u8.ToArray();
        var staged = new StagedUploadFile { FileName = fileName, Content = content, Length = content.Length };

        await using var db = NewDb(dbName);
        db.StagedUploadFiles.Add(staged);
        await db.SaveChangesAsync();

        return new StagedFileRef(staged.Id, staged.FileName, staged.Length);
    }

    private static UploadBankStatementCommand CommandFor(
        StagedFileRef fileRef, Guid companyId, string tenant = Studio, bool forceReapply = false)
        => new(Guid.NewGuid(), [fileRef], companyId, "AUTO", false, forceReapply, tenant);

    /// <summary>Movimiento tal como lo devolvería el parser: sin clasificar (la clasificación la hace el handler).</summary>
    private static BankTransaction ParsedTx(decimal amount, TransactionType type, string desc, DateOnly? date = null) => new()
    {
        Date        = date ?? new DateOnly(2025, 6, 15),
        Description = desc,
        Amount      = amount,
        Type        = type,
        Currency    = Currencies.Ars,
    };

    // ── 1. Deduplicación ────────────────────────────────────────────────────────

    [Fact]
    public async Task Duplicate_SameMovementUploadedTwice_IsSkippedOnSecondUpload()
    {
        var dbName = Guid.NewGuid().ToString();
        var company = NewCompany();
        await using (var seed = NewDb(dbName)) { seed.Companies.Add(company); await seed.SaveChangesAsync(); }

        // Cada llamada del parser devuelve un movimiento fresco con los mismos campos → misma firma.
        Func<IEnumerable<BankTransaction>> parsed = () => [ParsedTx(1000m, TransactionType.Debit, "PAGO PROVEEDOR")];

        var fileRef1 = await StageCsvAsync(dbName);
        Result<UploadBankStatementResponse> first;
        await using (var db = NewDb(dbName))
            first = await NewHandler(db, parsed).Handle(CommandFor(fileRef1, company.Id), CancellationToken.None);

        first.IsSuccess.Should().BeTrue();
        first.Value!.TotalProcessed.Should().Be(1);
        first.Value.DuplicatesSkipped.Should().Be(0);

        // Segunda subida idéntica: la firma coincide con la ya persistida → se descarta como duplicado.
        var fileRef2 = await StageCsvAsync(dbName);
        Result<UploadBankStatementResponse> second;
        await using (var db = NewDb(dbName))
            second = await NewHandler(db, parsed).Handle(CommandFor(fileRef2, company.Id), CancellationToken.None);

        second.IsSuccess.Should().BeTrue();
        second.Value!.TotalProcessed.Should().Be(0, "el movimiento ya existe en la BD");
        second.Value.DuplicatesSkipped.Should().Be(1);
        second.Value.SkippedDuplicates.Should().ContainSingle(d =>
            d.Amount == 1000m && d.Description == "PAGO PROVEEDOR" && d.Currency == Currencies.Ars);

        await using var assert = NewDb(dbName);
        (await assert.BankTransactions.CountAsync()).Should().Be(1, "el duplicado no debe persistirse");
    }

    // ── 2. Límite de cuota ──────────────────────────────────────────────────────

    [Fact]
    public async Task Quota_WhenTenantOverLimit_ReturnsPaymentRequired_AndPersistsNothing()
    {
        var dbName = Guid.NewGuid().ToString();
        var company = NewCompany();
        await using (var seed = NewDb(dbName)) { seed.Companies.Add(company); await seed.SaveChangesAsync(); }

        // La cuota solo cuenta movimientos del mes en curso: se datan hoy para forzar el chequeo.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        Func<IEnumerable<BankTransaction>> parsed = () => [ParsedTx(500m, TransactionType.Debit, "MOVIMIENTO DEL MES", today)];

        var fileRef = await StageCsvAsync(dbName);
        await using var db = NewDb(dbName);
        var result = await NewHandler(db, parsed, canUpload: false).Handle(CommandFor(fileRef, company.Id), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.StatusCode.Should().Be(402, "cuota excedida → Payment Required");
        result.Error.Should().StartWith("QUOTA_EXCEEDED|");

        await using var assert = NewDb(dbName);
        (await assert.BankTransactions.CountAsync()).Should().Be(0, "no se persiste nada si se rechaza por cuota");
    }

    // ── 3. Clasificación por reglas ─────────────────────────────────────────────

    [Fact]
    public async Task Classification_AppliesRule_AndAssignsAccountBeforePersisting()
    {
        var dbName = Guid.NewGuid().ToString();
        var company = NewCompany();
        var rule = new AccountingRule
        {
            CompanyId     = company.Id,
            Keyword       = "EDENOR",
            TargetAccount = "Servicios Públicos",
            Priority      = 1,
        };
        await using (var seed = NewDb(dbName))
        {
            seed.Companies.Add(company);
            seed.AccountingRules.Add(rule);
            await seed.SaveChangesAsync();
        }

        Func<IEnumerable<BankTransaction>> parsed = () => [ParsedTx(2500m, TransactionType.Debit, "PAGO EDENOR FACTURA")];

        var fileRef = await StageCsvAsync(dbName);
        await using (var db = NewDb(dbName))
        {
            var result = await NewHandler(db, parsed).Handle(CommandFor(fileRef, company.Id), CancellationToken.None);
            result.IsSuccess.Should().BeTrue();
            result.Value!.TotalProcessed.Should().Be(1);
        }

        await using var assert = NewDb(dbName);
        var persisted = await assert.BankTransactions.SingleAsync();
        persisted.AssignedAccount.Should().Be("Servicios Públicos", "la regla debe aplicarse antes de guardar");
        persisted.ClassificationSource.Should().Be(ClassificationSources.HardRule);
        persisted.CompanyId.Should().Be(company.Id);
    }

    // ── 4. Aislamiento de tenant ────────────────────────────────────────────────

    [Fact]
    public async Task ResolveCompany_WhenCompanyBelongsToAnotherTenant_ReturnsForbidden()
    {
        var dbName = Guid.NewGuid().ToString();
        var company = NewCompany();
        company.StudioTenantId = "otro-estudio"; // distinto del tenant que viaja en el command
        await using (var seed = NewDb(dbName)) { seed.Companies.Add(company); await seed.SaveChangesAsync(); }

        Func<IEnumerable<BankTransaction>> parsed = () => [ParsedTx(100m, TransactionType.Debit, "X")];

        var fileRef = await StageCsvAsync(dbName);
        await using var db = NewDb(dbName);
        var result = await NewHandler(db, parsed).Handle(CommandFor(fileRef, company.Id), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.StatusCode.Should().Be(403, "una empresa de otro estudio no es accesible");

        await using var assert = NewDb(dbName);
        (await assert.BankTransactions.CountAsync()).Should().Be(0);
    }

    // ── 5. Resultado durable del job + limpieza de staging ──────────────────────

    [Fact]
    public async Task Handle_OnCompletion_WritesJobResultAndCleansUpStagedFile()
    {
        var dbName = Guid.NewGuid().ToString();
        var company = NewCompany();
        await using (var seed = NewDb(dbName)) { seed.Companies.Add(company); await seed.SaveChangesAsync(); }

        Func<IEnumerable<BankTransaction>> parsed = () => [ParsedTx(100m, TransactionType.Debit, "X")];
        var fileRef = await StageCsvAsync(dbName);
        var command = CommandFor(fileRef, company.Id);

        await using (var db = NewDb(dbName))
            await NewHandler(db, parsed).Handle(command, CancellationToken.None);

        await using var assert = NewDb(dbName);
        var jobResult = await assert.UploadJobResults.SingleAsync(r => r.JobId == command.UploadId.ToString());
        jobResult.IsSuccess.Should().BeTrue();
        jobResult.StatusCode.Should().Be(200);
        jobResult.StudioTenantId.Should().Be(Studio);
        jobResult.ResultJson.Should().Contain("\"totalProcessed\":1");

        (await assert.StagedUploadFiles.AnyAsync(f => f.Id == fileRef.StagedFileId))
            .Should().BeFalse("el staging consumido debe borrarse tras procesar, sea cual sea el resultado");
    }
}
