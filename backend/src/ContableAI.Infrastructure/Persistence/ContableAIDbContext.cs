using ContableAI.Domain.Constants;
using ContableAI.Domain.Entities;
using ContableAI.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using System.Reflection;

namespace ContableAI.Infrastructure.Persistence;

public class ContableAIDbContext : DbContext
{
    // ── Aislamiento multi-tenant (Global Query Filters) ──────────────────────────
    // P-1: el contexto es POOLED — el constructor solo puede recibir las options (una
    // instancia se reutiliza entre requests), así que el tenant ya no se captura en el
    // ctor sino que se inyecta post-lease vía SetTenant() (registración scoped en
    // ServiceExtensions). El filtro referencia estos campos y EF los re-evalúa en cada
    // query, por lo que el modelo cacheado sirve a todas las instancias con su propio tenant.
    private string? _currentTenantId;

    // El filtro se DESACTIVA cuando no hay tenant (seed, background jobs, login) o
    // cuando el usuario es SystemAdmin (operador de plataforma, acceso cross-tenant).
    // Default true = sin tenant: cubre el uso directo (tests, design-time, jobs) donde
    // nadie llama a SetTenant.
    private bool _tenantFilterDisabled = true;

    public ContableAIDbContext(DbContextOptions<ContableAIDbContext> options) : base(options)
    {
    }

    /// <summary>
    /// Estampa el tenant del usuario autenticado en esta instancia (pooling-safe: se llama en
    /// CADA lease del pool, por lo que siempre pisa el estado del request anterior — nunca hay
    /// tenant residual). <c>null</c> o SystemAdmin desactivan el filtro.
    /// </summary>
    public void SetTenant(ICurrentTenantService? tenant)
    {
        _currentTenantId      = tenant?.StudioTenantId;
        _tenantFilterDisabled = _currentTenantId is null || (tenant?.IsSystemAdmin ?? false);
    }

    /// <summary>Mapeado a la función unaccent() de PostgreSQL (extensión unaccent).</summary>
    public static string Unaccent(string text) => throw new InvalidOperationException("Solo se puede usar en consultas LINQ-to-SQL.");

    public DbSet<BankTransaction>  BankTransactions  { get; set; }
    public DbSet<BankAccount>      BankAccounts      { get; set; }
    public DbSet<AccountingRule>   AccountingRules   { get; set; }
    public DbSet<Company>          Companies         { get; set; }
    public DbSet<User>             Users             { get; set; }
    public DbSet<ChartOfAccount>   ChartOfAccounts   { get; set; }
    public DbSet<JournalEntry>     JournalEntries    { get; set; }
    public DbSet<JournalEntryLine> JournalEntryLines { get; set; }
    public DbSet<AuditLog>         AuditLogs         { get; set; }
    public DbSet<ClosedPeriod>     ClosedPeriods     { get; set; }
    public DbSet<RuleSuggestion>   RuleSuggestions   { get; set; }
    public DbSet<AfipVoucher>      AfipVouchers      { get; set; }
    public DbSet<RefreshToken>     RefreshTokens     { get; set; }
    public DbSet<StagedUploadFile> StagedUploadFiles { get; set; }
    public DbSet<UploadJobResult>  UploadJobResults  { get; set; }
    public DbSet<UsageEvent>       UsageEvents       { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ==========================================
        // BankTransaction
        // ==========================================
        modelBuilder.Entity<BankTransaction>()
            .Property(b => b.Amount)
            .HasPrecision(18, 2);

        modelBuilder.Entity<BankTransaction>()
            .Property(b => b.BalanceAfter)
            .HasPrecision(18, 2);

        // Moneda (ISO 4217, 3 letras). Default ARS: backfillea las filas existentes en el
        // ALTER TABLE de la migración (metadata-only en Postgres 11+, sin rewrite).
        modelBuilder.Entity<BankTransaction>()
            .Property(b => b.Currency)
            .HasMaxLength(Currencies.CodeLength)
            .HasDefaultValue(Currencies.Ars);

        // Índice para el filtro por moneda de la grilla (Fase D).
        modelBuilder.Entity<BankTransaction>()
            .HasIndex(b => new { b.CompanyId, b.Currency })
            .HasDatabaseName("IX_BankTransactions_CompanyId_Currency");

        // P-2: columna desnormalizada del estudio — la usa el Global Query Filter para no
        // joinear a Companies en CADA query de transacciones. Se estampa al adoptar la
        // transacción en el upload; el backfill de datos históricos vive en la migración
        // DenormalizeStudioTenantIdOnBankTransactions.
        modelBuilder.Entity<BankTransaction>()
            .HasIndex(b => b.StudioTenantId)
            .HasDatabaseName("IX_BankTransactions_StudioTenantId");

        // P-4: índices de respaldo para los ordenamientos de la grilla. El sort por defecto
        // es (SortOrder, Date); el resto de los campos ordenables llevan índice compuesto con
        // CompanyId (la grilla siempre acota por empresa/estudio antes de ordenar).
        modelBuilder.Entity<BankTransaction>()
            .HasIndex(b => new { b.CompanyId, b.SortOrder, b.Date })
            .HasDatabaseName("IX_BankTransactions_CompanyId_SortOrder_Date");

        modelBuilder.Entity<BankTransaction>()
            .HasIndex(b => new { b.CompanyId, b.Amount })
            .HasDatabaseName("IX_BankTransactions_CompanyId_Amount");

        modelBuilder.Entity<BankTransaction>()
            .HasIndex(b => new { b.CompanyId, b.AssignedAccount })
            .HasDatabaseName("IX_BankTransactions_CompanyId_AssignedAccount");

        // Índice en TenantId (legacy) y en CompanyId (FK real)
        modelBuilder.Entity<BankTransaction>()
            .HasIndex(b => b.TenantId);

        modelBuilder.Entity<BankTransaction>()
            .HasIndex(b => b.CompanyId);

        // Índices compuestos de performance (reportes y paginación por período)
        modelBuilder.Entity<BankTransaction>()
            .HasIndex(b => new { b.CompanyId, b.Date })
            .HasDatabaseName("IX_BankTransactions_CompanyId_Date");

        modelBuilder.Entity<BankTransaction>()
            .HasIndex(b => new { b.CompanyId, b.ClassificationSource })
            .HasDatabaseName("IX_BankTransactions_CompanyId_ClassificationSource");

        // FK real: BankTransaction → Company
        modelBuilder.Entity<BankTransaction>()
            .HasOne(b => b.Company)
            .WithMany()
            .HasForeignKey(b => b.CompanyId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.SetNull);

        // ==========================================
        // BankAccount (F1 — multi-cuenta bancaria)
        // ==========================================
        modelBuilder.Entity<BankAccount>()
            .Property(a => a.Currency)
            .HasMaxLength(Currencies.CodeLength)
            .HasDefaultValue(Currencies.Ars);

        // Enrutamiento del OCR: el número normalizado identifica la cuenta DENTRO de la empresa
        // (dos empresas de un mismo grupo pueden compartir CBU, por eso no es único global).
        // NormalizedNumber es nullable y Postgres trata los NULL como distintos entre sí, así que
        // varias cuentas todavía sin número (las del backfill) conviven bajo este índice único.
        modelBuilder.Entity<BankAccount>()
            .HasIndex(a => new { a.CompanyId, a.NormalizedNumber })
            .IsUnique()
            .HasDatabaseName("IX_BankAccounts_CompanyId_NormalizedNumber");

        modelBuilder.Entity<BankAccount>()
            .HasIndex(a => a.StudioTenantId)
            .HasDatabaseName("IX_BankAccounts_StudioTenantId");

        modelBuilder.Entity<BankAccount>()
            .HasOne(a => a.Company)
            .WithMany()
            .HasForeignKey(a => a.CompanyId)
            .OnDelete(DeleteBehavior.Cascade);

        // Índices de los filtros por cuenta de ambas grillas (movimientos y libro diario).
        modelBuilder.Entity<BankTransaction>()
            .HasIndex(b => new { b.BankAccountId, b.Date })
            .HasDatabaseName("IX_BankTransactions_BankAccountId_Date");

        modelBuilder.Entity<JournalEntry>()
            .HasIndex(j => new { j.CompanyId, j.BankAccountId })
            .HasDatabaseName("IX_JournalEntries_CompanyId_BankAccountId");

        // ==========================================
        // AccountingRule
        // ==========================================
        modelBuilder.Entity<AccountingRule>()
            .HasIndex(r => r.CompanyId);

        // Índice compuesto de performance (clasificación en batch por prioridad)
        modelBuilder.Entity<AccountingRule>()
            .HasIndex(r => new { r.CompanyId, r.Priority })
            .HasDatabaseName("IX_AccountingRules_CompanyId_Priority");

        modelBuilder.Entity<AccountingRule>()
            .HasIndex(r => r.StudioTenantId)
            .HasDatabaseName("IX_AccountingRules_StudioTenantId");

        // ==========================================
        // Company
        // ==========================================
        modelBuilder.Entity<Company>()
            .HasIndex(c => c.StudioTenantId);

        // M-3: unicidad de CUIT POR ESTUDIO (no global): dos estudios pueden gestionar el
        // mismo contribuyente. El índice compuesto respalda a nivel de BD el chequeo del handler.
        modelBuilder.Entity<Company>()
            .HasIndex(c => new { c.StudioTenantId, c.Cuit })
            .IsUnique();

        // ==========================================
        // JournalEntry
        // ==========================================
        modelBuilder.Entity<JournalEntry>()
            .HasIndex(j => j.CompanyId);

        modelBuilder.Entity<JournalEntry>()
            .HasIndex(j => j.BankTransactionId)
            .IsUnique(); // una transacción → un asiento

        modelBuilder.Entity<JournalEntry>()
            .Property(j => j.Currency)
            .HasMaxLength(Currencies.CodeLength)
            .HasDefaultValue(Currencies.Ars);

        modelBuilder.Entity<JournalEntryLine>()
            .Property(l => l.Amount)
            .HasPrecision(18, 2);

        modelBuilder.Entity<JournalEntryLine>()
            .HasOne(l => l.JournalEntry)
            .WithMany(j => j.Lines)
            .HasForeignKey(l => l.JournalEntryId)
            .OnDelete(DeleteBehavior.Cascade);

        // ==========================================
        // ChartOfAccount
        // ==========================================
        modelBuilder.Entity<ChartOfAccount>()
            .HasIndex(a => new { a.Name, a.StudioTenantId })
            .IsUnique();

        modelBuilder.Entity<ChartOfAccount>()
            .HasIndex(a => a.StudioTenantId);

        // ==========================================
        // User
        // ==========================================
        // Email único por estudio contable
        modelBuilder.Entity<User>()
            .HasIndex(u => u.Email)
            .IsUnique();

        modelBuilder.Entity<User>()
            .HasIndex(u => u.StudioTenantId);

        // ==========================================
        // AuditLog
        // ==========================================
        modelBuilder.Entity<AuditLog>()
            .HasIndex(a => a.TenantId);

        modelBuilder.Entity<AuditLog>()
            .HasIndex(a => a.Timestamp);

        // ==========================================
        // ClosedPeriod
        // ==========================================
        modelBuilder.Entity<ClosedPeriod>()
            .HasIndex(p => new { p.StudioTenantId, p.Year, p.Month })
            .IsUnique(); // un estudio no puede cerrar el mismo mes dos veces

        // ==========================================
        // RuleSuggestion
        // ==========================================
        modelBuilder.Entity<RuleSuggestion>()
            .HasIndex(r => new { r.CompanyId, r.Status });

        // ==========================================
        // AfipVoucher
        // ==========================================
        modelBuilder.Entity<AfipVoucher>()
            .Property(v => v.Amount)
            .HasPrecision(18, 2);

        modelBuilder.Entity<AfipVoucher>()
            .Property(v => v.Currency)
            .HasMaxLength(Currencies.CodeLength)
            .HasDefaultValue(Currencies.Ars);

        // El índice único no incluye Currency: los VEPs de ARCA son siempre ARS.
        modelBuilder.Entity<AfipVoucher>()
            .HasIndex(v => new { v.CompanyId, v.Date, v.Amount, v.TaxName })
            .IsUnique();

        modelBuilder.Entity<AfipVoucher>()
            .HasOne(v => v.Company)
            .WithMany()
            .HasForeignKey(v => v.CompanyId)
            .OnDelete(DeleteBehavior.Cascade);

        // ==========================================
        // RefreshToken (A-3)
        // ==========================================
        modelBuilder.Entity<RefreshToken>()
            .HasIndex(t => t.TokenHash)
            .IsUnique();

        modelBuilder.Entity<RefreshToken>()
            .HasIndex(t => t.UserId);

        // ==========================================
        // StagedUploadFile / UploadJobResult
        // Tablas internas de soporte al pipeline asíncrono de subida de extractos (Hangfire).
        // Sin Global Query Filter: no son datos de negocio, se acceden siempre por Id/JobId
        // explícito desde el propio handler/endpoint, nunca listadas por tenant.
        // ==========================================
        modelBuilder.Entity<UploadJobResult>()
            .HasKey(r => r.JobId);

        modelBuilder.Entity<UploadJobResult>()
            .Property(r => r.ResultJson)
            .HasColumnType("jsonb");

        modelBuilder.Entity<UploadJobResult>()
            .HasIndex(r => r.StudioTenantId);

        // ==========================================
        // UsageEvent — ledger de facturación (append-only)
        // ==========================================

        // LA garantía de no cobrar dos veces el mismo extracto. Vive en la base y no en el código
        // a propósito: un "¿ya existe?" en C# tiene una ventana de carrera entre el SELECT y el
        // INSERT, y el pipeline de subida corre en jobs de Hangfire que pueden solaparse.
        // El tipo entra en la clave porque la misma identidad puede consumirse de formas distintas
        // (el hash de un extracto hoy factura su procesamiento; mañana podría facturar otra cosa).
        modelBuilder.Entity<UsageEvent>()
            .HasIndex(u => new { u.StudioTenantId, u.Type, u.IdempotencyKey })
            .IsUnique()
            .HasDatabaseName("UX_UsageEvents_Tenant_Type_IdempotencyKey");

        // Consulta de consumo del período: se filtra siempre por estudio + PeriodKey.
        modelBuilder.Entity<UsageEvent>()
            .HasIndex(u => new { u.StudioTenantId, u.PeriodKey })
            .HasDatabaseName("IX_UsageEvents_Tenant_PeriodKey");

        modelBuilder.Entity<UsageEvent>()
            .Property(u => u.StudioTenantId)
            .IsRequired()
            .HasMaxLength(120);

        // El SHA-256 en hexadecimal ocupa 64 caracteres; el margen deja lugar a otras identidades
        // (un GUID de movimiento, por ejemplo) sin migrar la columna.
        modelBuilder.Entity<UsageEvent>()
            .Property(u => u.IdempotencyKey)
            .IsRequired()
            .HasMaxLength(200);

        modelBuilder.Entity<UsageEvent>()
            .Property(u => u.PeriodKey)
            .IsRequired()
            .HasMaxLength(7); // "YYYY-MM"

        // Sin Global Query Filter, y es deliberado: el filtro por tenant se aplica explícitamente en
        // cada consulta de consumo. Un ledger de facturación tiene que poder leerse completo para
        // conciliar y auditar; que un filtro implícito esconda eventos sería el peor lugar posible
        // para una sorpresa.

        // Búsqueda sin distinción de tildes — P-3: mapea a f_unaccent(), el wrapper IMMUTABLE
        // de unaccent() creado en la migración AddTrigramSearchIndex. Tiene que ser la MISMA
        // expresión que la del índice trigram (GIN sobre f_unaccent("Description")): si la
        // query usara unaccent() y el índice f_unaccent(), el planner no lo aprovecharía.
        modelBuilder.HasDbFunction(
            typeof(ContableAIDbContext).GetMethod(nameof(Unaccent), BindingFlags.Public | BindingFlags.Static, [typeof(string)])!,
            b => b.HasName("f_unaccent"));

        // ==========================================
        // Optimistic Concurrency via xmin (PostgreSQL nativo)
        // xmin es el transaction ID interno de cada fila en PostgreSQL: se actualiza
        // automáticamente en cada escritura sin necesidad de una columna extra.
        // EF Core lanza DbUpdateConcurrencyException si el xmin no coincide al hacer UPDATE.
        // ==========================================
        modelBuilder.Entity<BankTransaction>()
            .Property<uint>("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();

        modelBuilder.Entity<JournalEntry>()
            .Property<uint>("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();

        // ==========================================
        // Global Query Filters — Aislamiento multi-tenant (fix IDOR/BOLA C-1)
        // ==========================================
        // Por defecto, TODA consulta LINQ sobre estas entidades queda acotada al estudio
        // (StudioTenantId) del usuario autenticado, cerrando la clase entera de accesos
        // cross-tenant por ID. El filtro se desactiva (_tenantFilterDisabled) cuando no hay
        // tenant (seed / background jobs / login) o el usuario es SystemAdmin.
        //
        // Contextos que necesitan saltear el filtro deliberadamente usan .IgnoreQueryFilters():
        //   · chequeo de unicidad GLOBAL de CUIT (coherente con el índice único de BD)
        //   · lecturas de deduplicación del UploadBankStatementHandler (ya auto-scoped por CompanyId)

        // Company: ancla directa por StudioTenantId (string).
        modelBuilder.Entity<Company>()
            .HasQueryFilter(c => _tenantFilterDisabled || c.StudioTenantId == _currentTenantId);

        // BankTransaction — P-2: el filtro ancla por la columna DESNORMALIZADA StudioTenantId
        // (antes joineaba a Companies vía la navegación en cada query de transacciones). Las
        // transacciones sin estudio estampado (legacy sin backfill posible, bucket
        // ESTUDIO_DEFAULT) quedan ocultas a los usuarios con tenant, igual que antes quedaban
        // las de CompanyId null.
        modelBuilder.Entity<BankTransaction>()
            .HasQueryFilter(b => _tenantFilterDisabled || b.StudioTenantId == _currentTenantId);

        // AccountingRule — cierra el IDOR que permitía leer, editar, desactivar o borrar una regla
        // de otro estudio conociendo su Id (los endpoints de /api/rules la buscaban solo por Id).
        //
        // Ancla por la columna DESNORMALIZADA StudioTenantId, que ahora llevan TODAS las reglas
        // (ver AccountingRule.StudioTenantId): una sola comparación de columna, sin joinear a
        // Companies en la carga de reglas del pipeline de clasificación.
        //
        // La regla de sistema se reconoce por su forma completa (sin empresa Y sin estudio) y no
        // solo por StudioTenantId == null: así, una fila anómala con empresa pero sin estudio queda
        // invisible en lugar de quedar visible para todos los estudios (fail-closed).
        modelBuilder.Entity<AccountingRule>()
            .HasQueryFilter(r => _tenantFilterDisabled
                              || (r.CompanyId == null && r.StudioTenantId == null)
                              || r.StudioTenantId == _currentTenantId);

        // BankAccount: ancla por la columna desnormalizada, igual que BankTransaction. Se define
        // desde el alta de la entidad para que ninguna consulta futura nazca sin aislamiento.
        modelBuilder.Entity<BankAccount>()
            .HasQueryFilter(a => _tenantFilterDisabled || a.StudioTenantId == _currentTenantId);
    }
}