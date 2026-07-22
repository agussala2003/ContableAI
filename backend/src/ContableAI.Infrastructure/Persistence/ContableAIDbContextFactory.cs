using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ContableAI.Infrastructure.Persistence;

/// <summary>
/// Factory de diseño usada exclusivamente por las herramientas de EF Core
/// (<c>dotnet ef migrations</c> / <c>database update</c>). No interviene en runtime
/// (la app usa <c>AddDbContext</c> en el contenedor de DI).
///
/// Existe para que el tooling NO tenga que arrancar <c>Program.cs</c> (que ejecuta
/// <c>SeedDatabaseAsync</c> → <c>MigrateAsync</c> y requeriría una BD viva). El scaffolding
/// de migraciones solo necesita el proveedor configurado, no una conexión real.
/// </summary>
public sealed class ContableAIDbContextFactory : IDesignTimeDbContextFactory<ContableAIDbContext>
{
    public ContableAIDbContext CreateDbContext(string[] args)
    {
        // Usa la connection string del entorno si está disponible; si no, un placeholder
        // (válido solo para generar migraciones, que no abren conexión).
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? "Host=localhost;Database=contableai_design;Username=postgres;Password=postgres";

        var options = new DbContextOptionsBuilder<ContableAIDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        // tenant = null → los Global Query Filters quedan desactivados (irrelevante en diseño).
        return new ContableAIDbContext(options);
    }
}
