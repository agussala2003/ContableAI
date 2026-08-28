using ContableAI.API.Endpoints;
using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.Server;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using Microsoft.AspNetCore.Http;

namespace ContableAI.Api.Tests;

/// <summary>
/// Regresión del incidente del 28/08/2026: "el sistema se queda procesando".
///
/// <c>GET /api/jobs/{id}/status</c> pedía una <see cref="IStorageConnection"/> al storage de
/// Hangfire y NO la disponía. Con el storage de PostgreSQL esa conexión es un préstamo del pool de
/// Npgsql, así que cada llamada filtraba una conexión. Y este endpoint es el que TODA la app
/// pollea cada 2-3 segundos (subida de extractos, generación de asientos, reaplicación de reglas):
/// una sola reaplicación que corre hasta el timeout de 5 minutos filtraba ~150 conexiones. Agotado
/// el pool, cualquier request posterior espera una conexión libre hasta el timeout.
///
/// Lo insidioso del bug es que la RESPUESTA seguía siendo correcta: ningún test funcional —ni el
/// endpoint devolviendo 200 con el estado bien— lo podía detectar. Por eso lo que se afirma acá es
/// el ciclo de vida del recurso, no el payload.
/// </summary>
public class JobStatusEndpointTests
{
    /// <summary>Conexión de mentira: lo único que interesa es si alguien la dispone.</summary>
    private sealed class TrackingConnection(JobData? jobData) : JobStorageConnection
    {
        public int DisposeCount { get; private set; }

        public override void Dispose()
        {
            DisposeCount++;
            base.Dispose();
        }

        public override JobData? GetJobData(string jobId) => jobData;

        // El resto de la superficie de IStorageConnection no participa de este test. Lanzar en vez
        // de devolver un default deja en evidencia si el endpoint empieza a usar algo más.
        public override IWriteOnlyTransaction CreateWriteTransaction() => throw new NotSupportedException();
        public override IDisposable AcquireDistributedLock(string resource, TimeSpan timeout) => throw new NotSupportedException();
        public override string CreateExpiredJob(Job job, IDictionary<string, string> parameters, DateTime createdAt, TimeSpan expireIn) => throw new NotSupportedException();
        public override IFetchedJob FetchNextJob(string[] queues, CancellationToken cancellationToken) => throw new NotSupportedException();
        public override void SetJobParameter(string id, string name, string value) => throw new NotSupportedException();
        public override string GetJobParameter(string id, string name) => throw new NotSupportedException();
        public override StateData GetStateData(string jobId) => throw new NotSupportedException();
        public override void AnnounceServer(string serverId, ServerContext context) => throw new NotSupportedException();
        public override void RemoveServer(string serverId) => throw new NotSupportedException();
        public override void Heartbeat(string serverId) => throw new NotSupportedException();
        public override int RemoveTimedOutServers(TimeSpan timeOut) => throw new NotSupportedException();
        public override HashSet<string> GetAllItemsFromSet(string key) => throw new NotSupportedException();
        public override string GetFirstByLowestScoreFromSet(string key, double fromScore, double toScore) => throw new NotSupportedException();
        public override void SetRangeInHash(string key, IEnumerable<KeyValuePair<string, string>> keyValuePairs) => throw new NotSupportedException();
        public override Dictionary<string, string> GetAllEntriesFromHash(string key) => throw new NotSupportedException();
    }

    private sealed class FakeStorage(TrackingConnection connection) : JobStorage
    {
        public override IStorageConnection GetConnection() => connection;
        public override IMonitoringApi GetMonitoringApi() => throw new NotSupportedException();
    }

    private static (TrackingConnection Connection, IResult Result) Invoke(JobData? jobData)
    {
        var connection = new TrackingConnection(jobData);
        var result = JobsEndpoints.GetJobStatus(new FakeStorage(connection), "job-1");
        return (connection, result);
    }

    [Fact]
    public void DisposesTheStorageConnection_WhenTheJobExists()
    {
        var (connection, _) = Invoke(new JobData { State = "Succeeded", CreatedAt = DateTime.UtcNow });

        connection.DisposeCount.Should().Be(1,
            "cada conexión que el endpoint pide al storage tiene que volver al pool de Npgsql");
    }

    [Fact]
    public void DisposesTheStorageConnection_OnTheNotFoundPathToo()
    {
        // El camino del 404 es el que más se recorre cuando algo va mal (un jobId viejo, un
        // storage recién purgado) y es justo el que tenía un `return` temprano.
        var (connection, result) = Invoke(null);

        connection.DisposeCount.Should().Be(1);
        result.Should().NotBeNull();
    }
}
