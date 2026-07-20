using ContableAI.Domain.Constants;
using ContableAI.Domain.Entities;
using ContableAI.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using System.Reflection;

namespace ContableAI.Infrastructure.Persistence;

public class ContableAIDbContext : DbContext
{
    // ── Aislamiento multi-tenant (Global Query Filters) ──────────────────────────
    // Se capturan en el ctor a partir del usuario autenticado (scoped, post-auth).
    // El filtro referencia estos campos y EF los re-evalúa en cada query, por lo que
    // el modelo cacheado sirve a todas las instancias con su propio tenant.
    private readonly string? _currentTenantId;

    // El filtro se DESACTIVA cuando no hay tenant (seed, background jobs, login) o
    // cuando el usuario es SystemAdmin (operador de plataforma, acceso cross-tenant).
    private readonly bool _tenantFilterDisabled;

    public ContableAIDbContext(
        DbContextOptions<ContableAIDbContext> options,
        ICurrentTenantService? tenant = null) : base(options)
    {
        _currentTenantId      = tenant?.StudioTenantId;
        _tenantFilterDisabled = _currentTenantId is null || (tenant?.IsSystemAdmin ?? false);
    }

    /// <summary>Mapeado a la función unaccent() de PostgreSQL (extensión unaccent).</summary>
    public static string Unaccent(string text) => throw new InvalidOperationException("Solo se puede usar en consultas LINQ-to-SQL.");

    public DbSet<BankTransaction>  BankTransactions  { get; set; }
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

        // Función unaccent de PostgreSQL para búsqueda sin distinción de tildes
        modelBuilder.HasDbFunction(
            typeof(ContableAIDbContext).GetMethod(nameof(Unaccent), BindingFlags.Public | BindingFlags.Static, [typeof(string)])!,
            b => b.HasName("unaccent"));

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

        // BankTransaction: sin columna de estudio propia → se ancla vía la navegación Company.
        // Las transacciones sin empresa (CompanyId null, bucket ESTUDIO_DEFAULT) quedan ocultas
        // a los usuarios con tenant, en línea con los listados que ya exigen CompanyId propio.
        modelBuilder.Entity<BankTransaction>()
            .HasQueryFilter(b => _tenantFilterDisabled
                                 || (b.Company != null && b.Company.StudioTenantId == _currentTenantId));
    }
}