using ContableAI.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ContableAI.Infrastructure.Persistence;

public class ContableAIDbContext : DbContext
{
    public ContableAIDbContext(DbContextOptions<ContableAIDbContext> options) : base(options) { }

    public DbSet<BankTransaction>  BankTransactions  { get; set; }
    public DbSet<AccountingRule>   AccountingRules   { get; set; }
    public DbSet<Company>          Companies         { get; set; }
    public DbSet<User>             Users             { get; set; }
    public DbSet<ChartOfAccount>   ChartOfAccounts   { get; set; }
    public DbSet<JournalEntry>     JournalEntries    { get; set; }
    public DbSet<JournalEntryLine> JournalEntryLines { get; set; }
    public DbSet<AuditLog>         AuditLogs         { get; set; }
    public DbSet<ClosedPeriod>     ClosedPeriods     { get; set; }

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

        // ==========================================
        // Company
        // ==========================================
        modelBuilder.Entity<Company>()
            .HasIndex(c => c.StudioTenantId);

        modelBuilder.Entity<Company>()
            .HasIndex(c => c.Cuit)
            .IsUnique();

        // ==========================================
        // JournalEntry
        // ==========================================
        modelBuilder.Entity<JournalEntry>()
            .HasIndex(j => j.CompanyId);

        modelBuilder.Entity<JournalEntry>()
            .HasIndex(j => j.BankTransactionId)
            .IsUnique(); // una transacción → un asiento

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
    }
}