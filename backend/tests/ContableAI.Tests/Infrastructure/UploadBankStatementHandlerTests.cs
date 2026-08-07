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

    /// <summary>
    /// Ignora el stream y devuelve movimientos frescos (el parseo real no es lo que se testea acá).
    /// <paramref name="detectedAccountNumber"/> simula lo que el OCR leyó del encabezado, que es
    /// lo que dispara cada uno de los flujos de enrutamiento.
    /// </summary>
    private sealed class FakeBankParser(
        Func<IEnumerable<BankTransaction>> factory,
        string? detectedAccountNumber = null,
        string? detectedCbu = null,
        IReadOnlyList<string>? conflictingIdentifiers = null) : IBankParserService
    {
        public ParsedStatement Parse(Stream fileStream, string bankCode, string fileName) =>
            new([.. factory()], Currencies.Ars, "BBVA", detectedAccountNumber, detectedCbu)
            {
                ConflictingAccountIdentifiers = conflictingIdentifiers ?? [],
            };

        public IEnumerable<BankTransaction> ParseCsv(Stream fileStream, string bankCode) => throw new NotSupportedException();
    }

    private sealed class FakeQuota(bool canUpload) : IQuotaService
    {
        public Task<bool> CanUploadTransactionsAsync(string studioTenantId, int count) => Task.FromResult(canUpload);
        public Task<QuotaLimits> GetLimitsAsync(string studioTenantId) => throw new NotSupportedException();
        public Task<QuotaUsage>  GetUsageAsync(string studioTenantId)  => throw new NotSupportedException();
        public Task<bool> CanAddCompanyAsync(string studioTenantId)          => throw new NotSupportedException();
        public Task<bool> CanAddRuleAsync(string studioTenantId, Guid companyId, int additional = 1) => throw new NotSupportedException();
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
        ContableAIDbContext db, Func<IEnumerable<BankTransaction>> parsed, bool canUpload = true,
        string? detectedAccountNumber = null, string? detectedCbu = null,
        IReadOnlyList<string>? conflictingIdentifiers = null)
        => new(
            db,
            new FakeBankParser(parsed, detectedAccountNumber, detectedCbu, conflictingIdentifiers),
            new ClassificationService(new HardRuleStrategy()), // motor de reglas real
            new FakeQuota(canUpload),
            new FakeJobClient(),
            NullLogger<UploadBankStatementHandler>.Instance);

    private static BankAccount NewBankAccount(
        Guid companyId, string alias, string? normalizedNumber = null, string? cbu = null) => new()
    {
        CompanyId         = companyId,
        Alias             = alias,
        NormalizedNumber  = normalizedNumber,
        Cbu               = cbu,
        Currency          = Currencies.Ars,
        ContraAccountName = "Banco Test",
        IsActive          = true,
        StudioTenantId    = Studio,
    };

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
        StagedFileRef fileRef, Guid companyId, string tenant = Studio, bool forceReapply = false,
        Guid? bankAccountId = null)
        => new(Guid.NewGuid(), [fileRef], companyId, "AUTO", false, forceReapply, tenant, bankAccountId);

    /// <summary>Movimiento tal como lo devolvería el parser: sin clasificar (la clasificación la hace el handler).</summary>
    private static BankTransaction ParsedTx(decimal amount, TransactionType type, string desc, DateOnly? date = null) => new()
    {
        Date        = date ?? new DateOnly(2025, 6, 15),
        Description = desc,
        Amount      = amount,
        Type        = type,
        Currency    = Currencies.Ars,
    };

    // ── 0. Enrutamiento a cuenta bancaria (F1.d) ────────────────────────────────

    [Fact]
    public async Task Routing_ExplicitBankAccount_WinsOverDetection()
    {
        var dbName = Guid.NewGuid().ToString();
        var company = NewCompany();
        var chosen   = NewBankAccount(company.Id, "Elegida a mano", normalizedNumber: "111");
        var detected = NewBankAccount(company.Id, "La que dice el PDF", normalizedNumber: "999");

        await using (var seed = NewDb(dbName))
        {
            seed.Companies.Add(company);
            seed.BankAccounts.AddRange(chosen, detected);
            await seed.SaveChangesAsync();
        }

        var fileRef = await StageCsvAsync(dbName);
        await using (var db = NewDb(dbName))
        {
            // El OCR lee "999", pero el usuario eligió la otra cuenta en la Dropzone.
            await NewHandler(db, () => [ParsedTx(100m, TransactionType.Debit, "PAGO")], detectedAccountNumber: "999")
                .Handle(CommandFor(fileRef, company.Id, bankAccountId: chosen.Id), CancellationToken.None);
        }

        await using var check = NewDb(dbName);
        var tx = await check.BankTransactions.SingleAsync();
        tx.BankAccountId.Should().Be(chosen.Id, "la elección explícita del usuario manda sobre el OCR");
    }

    [Fact]
    public async Task Routing_DetectedNumber_MatchingOneAccount_RoutesThere()
    {
        var dbName = Guid.NewGuid().ToString();
        var company = NewCompany();
        var galicia = NewBankAccount(company.Id, "Galicia", normalizedNumber: "42109");
        var bbva    = NewBankAccount(company.Id, "BBVA",    normalizedNumber: "1140071415");

        await using (var seed = NewDb(dbName))
        {
            seed.Companies.Add(company);
            seed.BankAccounts.AddRange(galicia, bbva);
            await seed.SaveChangesAsync();
        }

        var fileRef = await StageCsvAsync(dbName);
        await using (var db = NewDb(dbName))
        {
            await NewHandler(db, () => [ParsedTx(100m, TransactionType.Debit, "PAGO")], detectedAccountNumber: "1140071415")
                .Handle(CommandFor(fileRef, company.Id), CancellationToken.None);
        }

        await using var check = NewDb(dbName);
        (await check.BankTransactions.SingleAsync()).BankAccountId.Should().Be(bbva.Id);
    }

    [Fact]
    public async Task Routing_MatchesByCbu_WhenTheShortNumberIsNotDetected()
    {
        // Caso Mercado Pago del corpus: algunos extractos solo exponen el CVU.
        var dbName = Guid.NewGuid().ToString();
        var company = NewCompany();
        var mp = NewBankAccount(company.Id, "Mercado Pago", normalizedNumber: "100637858587", cbu: "0000003100075266676122");

        await using (var seed = NewDb(dbName))
        {
            seed.Companies.Add(company);
            seed.BankAccounts.Add(mp);
            await seed.SaveChangesAsync();
        }

        var fileRef = await StageCsvAsync(dbName);
        await using (var db = NewDb(dbName))
        {
            await NewHandler(db, () => [ParsedTx(100m, TransactionType.Credit, "COBRO")],
                    detectedAccountNumber: null, detectedCbu: "0000003100075266676122")
                .Handle(CommandFor(fileRef, company.Id), CancellationToken.None);
        }

        await using var check = NewDb(dbName);
        (await check.BankTransactions.SingleAsync()).BankAccountId.Should().Be(mp.Id,
            "no debe crear una cuenta nueva: el CBU ya identifica a una existente");
        (await check.BankAccounts.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Routing_UnknownNumber_CreatesProvisionalAccount_AndReportsIt()
    {
        var dbName = Guid.NewGuid().ToString();
        var company = NewCompany();
        var existing = NewBankAccount(company.Id, "Galicia", normalizedNumber: "42109");

        await using (var seed = NewDb(dbName))
        {
            seed.Companies.Add(company);
            seed.BankAccounts.Add(existing);
            await seed.SaveChangesAsync();
        }

        var fileRef = await StageCsvAsync(dbName);
        Result<UploadBankStatementResponse> result;
        await using (var db = NewDb(dbName))
        {
            result = await NewHandler(db, () => [ParsedTx(100m, TransactionType.Debit, "PAGO")], detectedAccountNumber: "5550001")
                .Handle(CommandFor(fileRef, company.Id), CancellationToken.None);
        }

        result.IsSuccess.Should().BeTrue();
        result.Value!.CreatedBankAccounts.Should().ContainSingle()
            .Which.AccountNumber.Should().Be("5550001");

        await using var check = NewDb(dbName);
        var created = await check.BankAccounts.SingleAsync(a => a.NormalizedNumber == "5550001");
        created.ContraAccountName.Should().BeEmpty("nace provisional: todavía no puede asentar");

        (await check.BankTransactions.SingleAsync()).BankAccountId.Should().Be(created.Id);
    }

    [Fact]
    public async Task Routing_AmbiguousMatch_RejectsTheFile()
    {
        var dbName = Guid.NewGuid().ToString();
        var company = NewCompany();
        // Dos cuentas alcanzables por el mismo identificador: una por número, otra por CBU.
        var byNumber = NewBankAccount(company.Id, "Cuenta A", normalizedNumber: "0000003100075266676122");
        var byCbu    = NewBankAccount(company.Id, "Cuenta B", normalizedNumber: "777", cbu: "0000003100075266676122");

        await using (var seed = NewDb(dbName))
        {
            seed.Companies.Add(company);
            seed.BankAccounts.AddRange(byNumber, byCbu);
            await seed.SaveChangesAsync();
        }

        var fileRef = await StageCsvAsync(dbName);
        Result<UploadBankStatementResponse> result;
        await using (var db = NewDb(dbName))
        {
            result = await NewHandler(db, () => [ParsedTx(100m, TransactionType.Debit, "PAGO")],
                    detectedCbu: "0000003100075266676122")
                .Handle(CommandFor(fileRef, company.Id), CancellationToken.None);
        }

        result.IsSuccess.Should().BeFalse("ningún archivo del lote pudo enrutarse");

        await using var check = NewDb(dbName);
        (await check.BankTransactions.CountAsync()).Should().Be(0,
            "enrutar a la cuenta equivocada contaminaría el libro diario de una cuenta ajena");
        (await check.BankAccounts.CountAsync()).Should().Be(2, "tampoco debe crear una cuenta nueva");
    }

    [Fact]
    public async Task Routing_NothingDetected_ButCompanyHasAccounts_RejectsTheFile()
    {
        var dbName = Guid.NewGuid().ToString();
        var company = NewCompany();
        await using (var seed = NewDb(dbName))
        {
            seed.Companies.Add(company);
            seed.BankAccounts.Add(NewBankAccount(company.Id, "Galicia", normalizedNumber: "42109"));
            await seed.SaveChangesAsync();
        }

        var fileRef = await StageCsvAsync(dbName);
        Result<UploadBankStatementResponse> result;
        await using (var db = NewDb(dbName))
        {
            result = await NewHandler(db, () => [ParsedTx(100m, TransactionType.Debit, "PAGO")])
                .Handle(CommandFor(fileRef, company.Id), CancellationToken.None);
        }

        result.IsSuccess.Should().BeFalse();

        await using var check = NewDb(dbName);
        (await check.BankTransactions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Routing_ConsolidatedStatement_RejectsTheFile()
    {
        // Caso "Cuenta Pyme" de BBVA: el encabezado lista dos cuentas con sus dos CBU. Enrutar todo
        // a la primera contaminaría el libro diario de una cuenta con movimientos de la otra.
        var dbName = Guid.NewGuid().ToString();
        var company = NewCompany();
        await using (var seed = NewDb(dbName))
        {
            seed.Companies.Add(company);
            seed.BankAccounts.Add(NewBankAccount(company.Id, "BBVA", normalizedNumber: "1840098978"));
            await seed.SaveChangesAsync();
        }

        var fileRef = await StageCsvAsync(dbName);
        Result<UploadBankStatementResponse> result;
        await using (var db = NewDb(dbName))
        {
            result = await NewHandler(
                    db,
                    () => [ParsedTx(100m, TransactionType.Debit, "PAGO")],
                    detectedAccountNumber: "1840098978",
                    conflictingIdentifiers: ["0170184120000000989781", "0170184120000000989859"])
                .Handle(CommandFor(fileRef, company.Id), CancellationToken.None);
        }

        result.IsSuccess.Should().BeFalse(
            "un resumen consolidado no se puede enrutar sin adivinar a qué cuenta va cada movimiento");
        result.Error.Should().Contain("resumen consolidado");

        await using var check = NewDb(dbName);
        (await check.BankTransactions.CountAsync()).Should().Be(0);
        (await check.BankAccounts.CountAsync()).Should().Be(1, "tampoco debe crear una cuenta provisional");
    }

    [Fact]
    public async Task Routing_ConsolidatedStatement_WithExplicitAccount_IsImported()
    {
        // La salida del rechazo anterior: el usuario afirma que todos los movimientos del archivo
        // son de una sola cuenta. Es la única forma de cargar un consolidado, y tiene que funcionar.
        var dbName = Guid.NewGuid().ToString();
        var company = NewCompany();
        var chosen = NewBankAccount(company.Id, "BBVA Cta. Cte.", normalizedNumber: "1840098978");

        await using (var seed = NewDb(dbName))
        {
            seed.Companies.Add(company);
            seed.BankAccounts.Add(chosen);
            await seed.SaveChangesAsync();
        }

        var fileRef = await StageCsvAsync(dbName);
        Result<UploadBankStatementResponse> result;
        await using (var db = NewDb(dbName))
        {
            result = await NewHandler(
                    db,
                    () => [ParsedTx(100m, TransactionType.Debit, "PAGO")],
                    conflictingIdentifiers: ["0170184120000000989781", "0170184120000000989859"])
                .Handle(CommandFor(fileRef, company.Id, bankAccountId: chosen.Id), CancellationToken.None);
        }

        result.IsSuccess.Should().BeTrue();

        await using var check = NewDb(dbName);
        (await check.BankTransactions.SingleAsync()).BankAccountId.Should().Be(chosen.Id);
    }

    [Fact]
    public async Task Routing_NothingDetected_AndCompanyHasNoAccounts_ImportsUnrouted()
    {
        // No hay a qué enrutar mal: es el comportamiento previo a la multi-cuenta, y el que
        // mantiene vivas las cargas de CSV/XLSX de empresas que aún no configuraron cuentas.
        var dbName = Guid.NewGuid().ToString();
        var company = NewCompany();
        await using (var seed = NewDb(dbName)) { seed.Companies.Add(company); await seed.SaveChangesAsync(); }

        var fileRef = await StageCsvAsync(dbName);
        Result<UploadBankStatementResponse> result;
        await using (var db = NewDb(dbName))
        {
            result = await NewHandler(db, () => [ParsedTx(100m, TransactionType.Debit, "PAGO")])
                .Handle(CommandFor(fileRef, company.Id), CancellationToken.None);
        }

        result.IsSuccess.Should().BeTrue();

        await using var check = NewDb(dbName);
        (await check.BankTransactions.SingleAsync()).BankAccountId.Should().BeNull();
    }

    [Fact]
    public async Task Dedup_SameMovementInTwoDifferentAccounts_IsNotADuplicate()
    {
        // EL caso que motiva incluir la cuenta en la firma: los dos extremos de una transferencia
        // entre cuentas propias son idénticos en fecha, importe y descripción. Sin la cuenta en la
        // firma, el segundo se descartaría y se perdería la mitad del movimiento.
        var dbName = Guid.NewGuid().ToString();
        var company = NewCompany();
        var galicia = NewBankAccount(company.Id, "Galicia", normalizedNumber: "111");
        var mp      = NewBankAccount(company.Id, "Mercado Pago", normalizedNumber: "222");

        await using (var seed = NewDb(dbName))
        {
            seed.Companies.Add(company);
            seed.BankAccounts.AddRange(galicia, mp);
            await seed.SaveChangesAsync();
        }

        Func<IEnumerable<BankTransaction>> movement =
            () => [ParsedTx(50_000m, TransactionType.Debit, "TRANSFERENCIA MISMA TITULARIDAD")];

        var fileA = await StageCsvAsync(dbName, "galicia.csv");
        await using (var db = NewDb(dbName))
            await NewHandler(db, movement, detectedAccountNumber: "111")
                .Handle(CommandFor(fileA, company.Id), CancellationToken.None);

        var fileB = await StageCsvAsync(dbName, "mercadopago.csv");
        Result<UploadBankStatementResponse> second;
        await using (var db = NewDb(dbName))
            second = await NewHandler(db, movement, detectedAccountNumber: "222")
                .Handle(CommandFor(fileB, company.Id), CancellationToken.None);

        second.IsSuccess.Should().BeTrue();
        second.Value!.DuplicatesSkipped.Should().Be(0,
            "el mismo importe en OTRA cuenta bancaria no es un duplicado");

        await using var check = NewDb(dbName);
        var txs = await check.BankTransactions.ToListAsync();
        txs.Should().HaveCount(2);
        txs.Select(t => t.BankAccountId).Should().OnlyHaveUniqueItems();
    }

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
