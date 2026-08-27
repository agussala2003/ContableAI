using ContableAI.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ContableAI.Tests.Infrastructure;

/// <summary>
/// <see cref="ContableAIDbContext"/> ajustado para correr sobre SQLite en memoria.
///
/// <b>Por qué SQLite y no el proveedor InMemory del resto de los tests:</b> InMemory no aplica
/// índices únicos ni restricciones. Cualquier test que quiera demostrar que una restricción de
/// unicidad HACE su trabajo —el caso del ledger de facturación— pasaría en verde contra InMemory
/// sin probar nada. SQLite las aplica de verdad y lanza la misma <c>DbUpdateException</c> que
/// PostgreSQL, que es lo que el código de producción atrapa.
///
/// El único ajuste es quitar el token de concurrencia <c>xmin</c>: es una columna de sistema de
/// PostgreSQL que el motor mantiene solo, y SQLite la materializaría como una columna común
/// <c>NOT NULL</c> sin valor, rompiendo cualquier INSERT. Se quita solo del modelo de prueba; el
/// mapeo de producción queda intacto.
/// </summary>
internal sealed class SqliteTestDbContext(DbContextOptions<ContableAIDbContext> options)
    : ContableAIDbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        foreach (var entity in modelBuilder.Model.GetEntityTypes())
            if (entity.FindProperty("xmin") is not null)
                entity.RemoveProperty("xmin");
    }
}

/// <summary>
/// Base de datos SQLite en memoria compartida por todos los contextos de un mismo test.
///
/// La conexión se mantiene abierta a mano porque SQLite descarta una base <c>:memory:</c> al
/// cerrarse la última conexión: sin esto, cada <c>new</c> del contexto vería una base vacía.
/// </summary>
internal sealed class SqliteTestDb : IDisposable
{
    private readonly SqliteConnection _connection;

    public SqliteTestDb()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        using var db = NewContext();
        db.Database.EnsureCreated();
    }

    /// <summary>Un contexto nuevo sobre la misma base. Cada uno tiene su propio change tracker.</summary>
    public ContableAIDbContext NewContext() =>
        new SqliteTestDbContext(
            new DbContextOptionsBuilder<ContableAIDbContext>().UseSqlite(_connection).Options);

    public void Dispose() => _connection.Dispose();
}
