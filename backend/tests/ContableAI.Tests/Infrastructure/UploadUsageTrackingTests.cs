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
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContableAI.Tests.Infrastructure;

/// <summary>
/// La pregunta que cierra la Fase D: <b>¿el pipeline completo evita cobrar dos veces el mismo
/// extracto?</b> Estos tests suben el mismo archivo dos veces, como haría un usuario que no está
/// seguro de si la primera carga funcionó.
///
/// Corren sobre <b>SQLite</b> y no sobre el proveedor InMemory del resto de los tests del handler,
/// por el mismo motivo que <see cref="UsageLedgerTests"/>: InMemory no aplica índices únicos, así
/// que contra ese proveedor el segundo cobro entraría y el test pasaría en verde igual. La
/// respuesta que da este archivo solo vale porque la restricción se ejecuta de verdad.
/// </summary>
public class UploadUsageTrackingTests : IDisposable
{
    private const string Studio = "ESTUDIO_TEST";

    private readonly SqliteTestDb _sqlite = new();

    public void Dispose() => _sqlite.Dispose();

    private ContableAIDbContext NewDb() => _sqlite.NewContext();

    // ── Fakes mínimos ───────────────────────────────────────────────────────────

    private sealed class FakeBankParser(Func<IEnumerable<BankTransaction>> factory) : IBankParserService
    {
        public ParsedStatement Parse(Stream fileStream, string bankCode, string fileName) =>
            new([.. factory()], Currencies.Ars, "BBVA", null, null);

        public IEnumerable<BankTransaction> ParseCsv(Stream fileStream, string bankCode) =>
            throw new NotSupportedException();
    }

    private sealed class FakeQuota : IQuotaService
    {
        public Task<bool> CanUploadTransactionsAsync(string studioTenantId, int count) => Task.FromResult(true);
        public Task<QuotaLimits> GetLimitsAsync(string studioTenantId) => throw new NotSupportedException();
        public Task<QuotaUsage>  GetUsageAsync(string studioTenantId)  => throw new NotSupportedException();
        public Task<bool> CanAddCompanyAsync(string studioTenantId)    => throw new NotSupportedException();
        public Task<bool> CanAddRuleAsync(string studioTenantId, Guid companyId, int additional = 1) => throw new NotSupportedException();
    }

    private sealed class FakeJobClient : IBackgroundJobClient
    {
        public string Create(Job job, IState state) => Guid.NewGuid().ToString();
        public bool ChangeState(string jobId, IState state, string expectedState) => true;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private UploadBankStatementHandler NewHandler(ContableAIDbContext db, Func<IEnumerable<BankTransaction>> parsed) =>
        new(db,
            new FakeBankParser(parsed),
            new ClassificationService(new HardRuleStrategy()),
            new FakeQuota(),
            new UsageService(db, NullLogger<UsageService>.Instance),
            new FakeJobClient(),
            NullLogger<UploadBankStatementHandler>.Instance);

    private static Company NewCompany() => new()
    {
        Name = "Empresa Test", Cuit = "30111111118", StudioTenantId = Studio, IsActive = true,
    };

    private static BankTransaction ParsedTx(decimal amount, string desc) => new()
    {
        Date        = new DateOnly(2025, 6, 15),
        Description = desc,
        Amount      = amount,
        Type        = TransactionType.Credit,
        Currency    = Currencies.Ars,
    };

    /// <summary>Deja un archivo en staging y devuelve su referencia. Cada subida stagea el suyo.</summary>
    private async Task<StagedFileRef> StageAsync(byte[] content, string fileName)
    {
        await using var db = NewDb();
        var staged = new StagedUploadFile { FileName = fileName, Content = content, Length = content.Length };
        db.StagedUploadFiles.Add(staged);
        await db.SaveChangesAsync();
        return new StagedFileRef(staged.Id, staged.FileName, staged.Length);
    }

    private static UploadBankStatementCommand CommandFor(StagedFileRef fileRef, Guid companyId) =>
        new(Guid.NewGuid(), [fileRef], companyId, "AUTO", false, false, Studio, null);

    private async Task<Guid> SeedCompanyAsync()
    {
        await using var db = NewDb();
        var company = NewCompany();
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        return company.Id;
    }

    // ── Los tests ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task SameFileUploadedTwice_IsChargedOnce()
    {
        var companyId = await SeedCompanyAsync();
        var content   = "EXTRACTO SEPTIEMBRE 2025 — contenido de prueba"u8.ToArray();

        // Primera subida.
        await using (var db = NewDb())
        {
            var fileRef = await StageAsync(content, "extracto.pdf");
            await NewHandler(db, () => [ParsedTx(1000m, "Transferencia recibida")])
                .Handle(CommandFor(fileRef, companyId), CancellationToken.None);
        }

        // Segunda subida del MISMO archivo: otro job, otra fila de staging, mismos bytes.
        await using (var db = NewDb())
        {
            var fileRef = await StageAsync(content, "extracto.pdf");
            await NewHandler(db, () => [ParsedTx(1000m, "Transferencia recibida")])
                .Handle(CommandFor(fileRef, companyId), CancellationToken.None);
        }

        await using var check = NewDb();
        check.UsageEvents.Count().Should().Be(1,
            "el segundo intento choca contra el índice único y no genera un segundo cobro");

        var usage = await new UsageService(check, NullLogger<UsageService>.Instance)
            .GetCurrentPeriodAsync(Studio);

        usage.StatementsProcessed.Should().Be(1);
    }

    [Fact]
    public async Task SameFileRenamed_IsStillChargedOnce()
    {
        var companyId = await SeedCompanyAsync();
        var content   = "MISMO CONTENIDO, OTRO NOMBRE"u8.ToArray();

        foreach (var name in new[] { "extracto.pdf", "extracto (1).pdf" })
        {
            await using var db = NewDb();
            var fileRef = await StageAsync(content, name);
            await NewHandler(db, () => [ParsedTx(500m, "Pago")])
                .Handle(CommandFor(fileRef, companyId), CancellationToken.None);
        }

        await using var check = NewDb();
        check.UsageEvents.Count().Should().Be(1,
            "la idempotencia es por contenido: renombrar el archivo no habilita un segundo cobro");
    }

    [Fact]
    public async Task DifferentFiles_AreChargedSeparately()
    {
        var companyId = await SeedCompanyAsync();

        foreach (var text in new[] { "EXTRACTO SEPTIEMBRE", "EXTRACTO OCTUBRE" })
        {
            await using var db = NewDb();
            var fileRef = await StageAsync(System.Text.Encoding.UTF8.GetBytes(text), $"{text}.pdf");
            await NewHandler(db, () => [ParsedTx(750m, "Movimiento")])
                .Handle(CommandFor(fileRef, companyId), CancellationToken.None);
        }

        await using var check = NewDb();
        check.UsageEvents.Count().Should().Be(2, "dos extractos distintos son dos consumos");
    }

    [Fact]
    public async Task FileThatParsesNothing_IsNotCharged()
    {
        var companyId = await SeedCompanyAsync();

        await using (var db = NewDb())
        {
            var fileRef = await StageAsync("EXTRACTO SIN MOVIMIENTOS"u8.ToArray(), "vacio.pdf");
            await NewHandler(db, () => []) // el parser no devuelve ni un movimiento
                .Handle(CommandFor(fileRef, companyId), CancellationToken.None);
        }

        await using var check = NewDb();
        check.UsageEvents.Should().BeEmpty(
            "un archivo del que no salió ningún movimiento no le entregó nada al usuario");
    }

    [Fact]
    public async Task ChargedEvent_CarriesTheBillingMetadata()
    {
        var companyId = await SeedCompanyAsync();
        var content   = "EXTRACTO CON METADATA"u8.ToArray();

        await using (var db = NewDb())
        {
            var fileRef = await StageAsync(content, "extracto.pdf");
            await NewHandler(db, () => [ParsedTx(1234m, "Acreditación")])
                .Handle(CommandFor(fileRef, companyId), CancellationToken.None);
        }

        await using var check = NewDb();
        var evt = check.UsageEvents.Single();

        evt.StudioTenantId.Should().Be(Studio, "se factura al estudio, no a la empresa");
        evt.CompanyId.Should().Be(companyId, "pero se guarda la empresa para poder desglosar");
        evt.Type.Should().Be(UsageEventType.StatementProcessed);
        evt.Quantity.Should().Be(1);
        evt.PeriodKey.Should().Be(UsageEvent.PeriodKeyOf(DateTime.UtcNow));
        evt.IdempotencyKey.Should().Be(UsageService.ComputeFileHash(content));
    }

    [Fact]
    public async Task LedgerFailure_NeverCostsTheUserTheirTransactions()
    {
        var companyId = await SeedCompanyAsync();
        var content   = "EXTRACTO IMPORTANTE"u8.ToArray();

        // Se registra el consumo a mano ANTES de subir, para forzar el choque del ledger durante
        // la carga: el escenario que no puede terminar con el contador perdiendo sus movimientos.
        await using (var db = NewDb())
        {
            await new UsageService(db, NullLogger<UsageService>.Instance)
                .TrackStatementProcessedAsync(Studio, companyId, UsageService.ComputeFileHash(content));
        }

        await using (var db = NewDb())
        {
            var fileRef = await StageAsync(content, "extracto.pdf");
            var result = await NewHandler(db, () => [ParsedTx(9999m, "Movimiento crítico")])
                .Handle(CommandFor(fileRef, companyId), CancellationToken.None);

            result.IsSuccess.Should().BeTrue("un choque del ledger no puede hacer fallar la carga");
        }

        await using var check = NewDb();
        check.BankTransactions.Count().Should().Be(1, "el movimiento tiene que haberse guardado igual");
        check.UsageEvents.Count().Should().Be(1, "y no tiene que haberse cobrado dos veces");
    }
}
