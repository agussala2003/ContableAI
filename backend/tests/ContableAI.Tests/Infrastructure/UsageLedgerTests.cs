using ContableAI.Domain.Entities;
using ContableAI.Domain.Enums;
using ContableAI.Infrastructure.Persistence;
using ContableAI.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContableAI.Tests.Infrastructure;

/// <summary>
/// Ledger de facturación: la garantía de no cobrar dos veces el mismo extracto.
///
/// Corren sobre <b>SQLite en memoria</b>, no sobre el proveedor InMemory que usa el resto de los
/// tests. El motivo es central: <b>InMemory NO aplica índices únicos</b>, así que contra ese
/// proveedor este archivo pasaría en verde sin probar absolutamente nada — el segundo insert
/// entraría y nadie se enteraría hasta facturarle de más a un cliente. SQLite sí los aplica y
/// lanza la misma <see cref="DbUpdateException"/> que Postgres.
/// </summary>
public class UsageLedgerTests : IDisposable
{
    private readonly SqliteTestDb _sqlite = new();

    public void Dispose() => _sqlite.Dispose();

    private ContableAIDbContext NewDb() => _sqlite.NewContext();

    private static UsageService NewService(ContableAIDbContext db) =>
        new(db, NullLogger<UsageService>.Instance);

    private const string Tenant = "ESTUDIO_A";
    private const string OtherTenant = "ESTUDIO_B";

    // ── Idempotencia ────────────────────────────────────────────────────────────

    [Fact]
    public async Task SameStatementTwice_IsChargedOnce()
    {
        using var db = NewDb();
        var usage = NewService(db);
        var company = Guid.NewGuid();
        var hash = UsageService.ComputeFileHash("extracto-septiembre"u8.ToArray());

        var first  = await usage.TrackStatementProcessedAsync(Tenant, company, hash);
        var second = await usage.TrackStatementProcessedAsync(Tenant, company, hash);

        first.Should().BeTrue("la primera vez el extracto se cobra");
        second.Should().BeFalse("la segunda vez ya estaba registrado y no se vuelve a cobrar");

        db.UsageEvents.Count().Should().Be(1, "el ledger tiene que quedar con un solo evento");
    }

    [Fact]
    public async Task DuplicateAttempt_DoesNotPoisonTheChangeTracker()
    {
        using var db = NewDb();
        var usage = NewService(db);
        var hash = UsageService.ComputeFileHash("mismo-archivo"u8.ToArray());

        await usage.TrackStatementProcessedAsync(Tenant, null, hash);
        await usage.TrackStatementProcessedAsync(Tenant, null, hash);

        // Si el evento rechazado quedara Added en el tracker, el próximo SaveChanges volvería a
        // intentar insertarlo y arrastraría el error a una operación que no tiene nada que ver.
        db.ChangeTracker.Entries<UsageEvent>()
            .Should().NotContain(e => e.State == EntityState.Added);

        var another = await usage.TrackStatementProcessedAsync(
            Tenant, null, UsageService.ComputeFileHash("otro-archivo"u8.ToArray()));

        another.Should().BeTrue("un extracto distinto tiene que poder registrarse después del choque");
        db.UsageEvents.Count().Should().Be(2);
    }

    [Fact]
    public async Task DifferentStatements_AreChargedSeparately()
    {
        using var db = NewDb();
        var usage = NewService(db);

        await usage.TrackStatementProcessedAsync(Tenant, null, UsageService.ComputeFileHash("septiembre"u8.ToArray()));
        await usage.TrackStatementProcessedAsync(Tenant, null, UsageService.ComputeFileHash("octubre"u8.ToArray()));

        db.UsageEvents.Count().Should().Be(2);
    }

    [Fact]
    public async Task SameStatementInTwoStudios_IsChargedToEach()
    {
        using var db = NewDb();
        var usage = NewService(db);
        var hash = UsageService.ComputeFileHash("extracto-compartido"u8.ToArray());

        var a = await usage.TrackStatementProcessedAsync(Tenant, null, hash);
        var b = await usage.TrackStatementProcessedAsync(OtherTenant, null, hash);

        // El estudio entra en la clave única: dos estudios que procesan un archivo idéntico hicieron
        // el trabajo dos veces, y cada uno paga el suyo.
        a.Should().BeTrue();
        b.Should().BeTrue();
        db.UsageEvents.Count().Should().Be(2);
    }

    [Fact]
    public async Task FileHash_IsContentBased_NotNameBased()
    {
        var a = UsageService.ComputeFileHash("contenido-identico"u8.ToArray());
        var b = UsageService.ComputeFileHash("contenido-identico"u8.ToArray());
        var c = UsageService.ComputeFileHash("contenido-distinto"u8.ToArray());

        // Renombrar el archivo y volver a subirlo no puede cobrar de nuevo: el hash es del contenido.
        a.Should().Be(b);
        a.Should().NotBe(c);
        a.Should().HaveLength(64, "SHA-256 en hexadecimal");
    }

    // ── Lectura del consumo ─────────────────────────────────────────────────────

    [Fact]
    public async Task CurrentPeriod_CountsOnlyThisStudio()
    {
        using var db = NewDb();
        var usage = NewService(db);

        await usage.TrackStatementProcessedAsync(Tenant, null, "hash-1");
        await usage.TrackStatementProcessedAsync(Tenant, null, "hash-2");
        await usage.TrackStatementProcessedAsync(OtherTenant, null, "hash-3");

        var mine = await usage.GetCurrentPeriodAsync(Tenant);

        mine.StatementsProcessed.Should().Be(2);
        mine.PeriodKey.Should().Be(UsageEvent.PeriodKeyOf(DateTime.UtcNow));
    }

    [Fact]
    public async Task CurrentPeriod_IgnoresOtherPeriods()
    {
        using var db = NewDb();
        var usage = NewService(db);

        await usage.TrackStatementProcessedAsync(Tenant, null, "de-este-mes");

        // Evento del mes pasado, insertado directo: el servicio siempre estampa el período actual.
        var lastMonth = DateTime.UtcNow.AddMonths(-1);
        db.UsageEvents.Add(new UsageEvent
        {
            StudioTenantId = Tenant,
            Type           = UsageEventType.StatementProcessed,
            Quantity       = 1,
            OccurredAt     = lastMonth,
            PeriodKey      = UsageEvent.PeriodKeyOf(lastMonth),
            IdempotencyKey = "del-mes-pasado",
        });
        await db.SaveChangesAsync();

        var current = await usage.GetCurrentPeriodAsync(Tenant);

        current.StatementsProcessed.Should().Be(1, "el consumo se factura por período, no acumulado");
    }

    [Fact]
    public async Task CurrentPeriod_SubtractsReversals()
    {
        using var db = NewDb();
        var usage = NewService(db);

        await usage.TrackStatementProcessedAsync(Tenant, null, "cobrado");

        // Un reverso es un evento NUEVO de cantidad negativa, nunca un DELETE: el ledger es
        // append-only y tiene que explicar cómo se llegó al total.
        db.UsageEvents.Add(new UsageEvent
        {
            StudioTenantId = Tenant,
            Type           = UsageEventType.StatementProcessed,
            Quantity       = -1,
            OccurredAt     = DateTime.UtcNow,
            PeriodKey      = UsageEvent.PeriodKeyOf(DateTime.UtcNow),
            IdempotencyKey = "reverso-de-cobrado",
        });
        await db.SaveChangesAsync();

        var current = await usage.GetCurrentPeriodAsync(Tenant);

        current.StatementsProcessed.Should().Be(0,
            "sumar Quantity y no contar filas es lo que hace que un reverso reste");
    }

    [Fact]
    public async Task CurrentPeriod_WithoutEvents_IsZeroNotNull()
    {
        using var db = NewDb();
        var usage = NewService(db);

        var current = await usage.GetCurrentPeriodAsync("ESTUDIO_SIN_CONSUMO");

        current.StatementsProcessed.Should().Be(0);
    }

    // ── El índice, no el código, es la garantía ─────────────────────────────────

    [Fact]
    public async Task UniqueIndex_RejectsTheDuplicateAtTheDatabaseLevel()
    {
        using var db = NewDb();

        UsageEvent Duplicate() => new()
        {
            StudioTenantId = Tenant,
            Type           = UsageEventType.StatementProcessed,
            Quantity       = 1,
            OccurredAt     = DateTime.UtcNow,
            PeriodKey      = UsageEvent.PeriodKeyOf(DateTime.UtcNow),
            IdempotencyKey = "hash-repetido",
        };

        db.UsageEvents.Add(Duplicate());
        await db.SaveChangesAsync();

        db.UsageEvents.Add(Duplicate());

        // Se verifica saltando el servicio a propósito: la garantía tiene que estar en la base, no
        // en un `if` de C#. Dos jobs de Hangfire concurrentes pueden atravesar cualquier chequeo
        // previo en código; la restricción única, no.
        var insert = async () => await db.SaveChangesAsync();
        await insert.Should().ThrowAsync<DbUpdateException>();
    }

    // ── Saldo prepago (D5) ──────────────────────────────────────────────────────

    [Fact]
    public async Task Balance_IsTopUpsMinusConsumption()
    {
        using var db = NewDb();
        var usage = NewService(db);

        await usage.AddQuotaAsync(Tenant, 50, "COMPROBANTE-001");
        await usage.TrackStatementProcessedAsync(Tenant, null, "extracto-1");
        await usage.TrackStatementProcessedAsync(Tenant, null, "extracto-2");

        (await usage.GetAvailableQuotaAsync(Tenant)).Should().Be(48);
    }

    [Fact]
    public async Task Balance_OfAStudioThatNeverBought_IsZero()
    {
        using var db = NewDb();

        // Sin eventos, SUM devuelve NULL en SQL. Si no se maneja, la materialización a int revienta
        // justo para el estudio recién creado — el peor momento posible.
        (await NewService(db).GetAvailableQuotaAsync("ESTUDIO_NUEVO")).Should().Be(0);
    }

    [Fact]
    public async Task Balance_DoesNotExpireAcrossPeriods()
    {
        using var db = NewDb();
        var usage = NewService(db);

        // Carga de hace tres meses: la promesa comercial es que el saldo NO vence.
        var old = DateTime.UtcNow.AddMonths(-3);
        db.UsageEvents.Add(new UsageEvent
        {
            StudioTenantId = Tenant,
            Type           = UsageEventType.StatementQuotaTopUp,
            Quantity       = 20,
            OccurredAt     = old,
            PeriodKey      = UsageEvent.PeriodKeyOf(old),
            IdempotencyKey = "TOPUP_VIEJO",
        });
        await db.SaveChangesAsync();

        (await usage.GetAvailableQuotaAsync(Tenant)).Should().Be(20);
    }

    [Fact]
    public async Task TopUp_IsIdempotentByPaymentReference()
    {
        using var db = NewDb();
        var usage = NewService(db);

        var first  = await usage.AddQuotaAsync(Tenant, 50, "PAGO-12345");
        var second = await usage.AddQuotaAsync(Tenant, 50, "PAGO-12345");

        first.Should().BeTrue();
        second.Should().BeFalse("el mismo comprobante no se acredita dos veces");

        (await usage.GetAvailableQuotaAsync(Tenant)).Should().Be(50, "no 100");
    }

    [Fact]
    public async Task TopUp_SameReferenceOnAnotherDay_StillDoesNotDoubleCredit()
    {
        using var db = NewDb();
        var usage = NewService(db);

        // Este es el caso que rompía la clave que incluía la fecha: el admin reintenta la carga al
        // día siguiente, o el reintento cruza la medianoche UTC. La clave es la referencia SOLA, así
        // que el resultado no depende de cuándo se ejecute.
        await usage.AddQuotaAsync(Tenant, 50, "PAGO-12345");

        var sameReferenceLater = await usage.AddQuotaAsync(Tenant, 50, "PAGO-12345");

        sameReferenceLater.Should().BeFalse();
        (await usage.GetAvailableQuotaAsync(Tenant)).Should().Be(50);
    }

    [Fact]
    public async Task TopUp_TrimsTheReference_SoAStraySpaceIsNotASecondCredit()
    {
        using var db = NewDb();
        var usage = NewService(db);

        await usage.AddQuotaAsync(Tenant, 20, "PAGO-999");
        var withSpaces = await usage.AddQuotaAsync(Tenant, 20, "  PAGO-999  ");

        withSpaces.Should().BeFalse();
        (await usage.GetAvailableQuotaAsync(Tenant)).Should().Be(20);
    }

    [Fact]
    public async Task TopUp_IsScopedToItsStudio()
    {
        using var db = NewDb();
        var usage = NewService(db);

        await usage.AddQuotaAsync(Tenant, 50, "PAGO-A");

        (await usage.GetAvailableQuotaAsync(OtherTenant)).Should().Be(0,
            "el saldo de un estudio no puede filtrarse a otro");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public async Task TopUp_RejectsNonPositiveAmounts(int amount)
    {
        using var db = NewDb();
        var usage = NewService(db);

        // Una carga negativa restaría saldo disfrazada de acreditación, sin dejar rastro de ajuste.
        var act = async () => await usage.AddQuotaAsync(Tenant, amount, "PAGO-X");
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task TopUp_RejectsAnEmptyReference(string reference)
    {
        using var db = NewDb();
        var usage = NewService(db);

        // Sin referencia todas las cargas compartirían clave: la segunda de un mismo estudio se
        // perdería en silencio. Es un error de operación, no un caso a tolerar.
        var act = async () => await usage.AddQuotaAsync(Tenant, 50, reference);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Reversal_GivesBackBalance()
    {
        using var db = NewDb();
        var usage = NewService(db);

        await usage.AddQuotaAsync(Tenant, 10, "PAGO-R");
        await usage.TrackStatementProcessedAsync(Tenant, null, "extracto-mal-procesado");

        (await usage.GetAvailableQuotaAsync(Tenant)).Should().Be(9);

        // El reverso es un CONSUMO de cantidad negativa, no un borrado ni una carga: el ledger
        // conserva los dos hechos y explica cómo se llegó al saldo.
        db.UsageEvents.Add(new UsageEvent
        {
            StudioTenantId = Tenant,
            Type           = UsageEventType.StatementProcessed,
            Quantity       = -1,
            OccurredAt     = DateTime.UtcNow,
            PeriodKey      = UsageEvent.PeriodKeyOf(DateTime.UtcNow),
            IdempotencyKey = "reverso-extracto-mal-procesado",
        });
        await db.SaveChangesAsync();

        (await usage.GetAvailableQuotaAsync(Tenant)).Should().Be(10);
    }
}
