using Hangfire;
using Hangfire.Storage;

namespace ContableAI.API.Endpoints;

public static class JobsEndpoints
{
    public static void MapJobsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/jobs/{jobId}/status", (string jobId) => GetJobStatus(JobStorage.Current, jobId))
        .WithName("GetJobStatus")
        .WithTags("Jobs")
        .WithSummary("Obtener el estado de un trabajo en segundo plano (Hangfire).")
        .Produces(200)
        .Produces(404);
    }

    /// <summary>
    /// Estado de un job de Hangfire.
    ///
    /// Vive fuera del lambda y recibe el <see cref="JobStorage"/> por parámetro (en vez de leer
    /// <c>JobStorage.Current</c> adentro) para que un test pueda inyectar un storage de mentira y
    /// verificar que la conexión se dispone. No es una abstracción de más: es la única forma de
    /// cubrir el bug que este método tuvo, porque la fuga no cambia la RESPUESTA — el endpoint
    /// devolvía exactamente lo mismo, filtrando una conexión por llamada.
    /// </summary>
    internal static IResult GetJobStatus(JobStorage storage, string jobId)
    {
        // `using` OBLIGATORIO: en el storage de PostgreSQL, IStorageConnection es un préstamo del
        // pool de Npgsql — no un objeto suelto. Sin disponerlo, cada llamada se quedaba con una
        // conexión para siempre, y este endpoint es el que TODA la app pollea cada 2-3 segundos
        // (subida de extractos, generación de asientos, reaplicación de reglas): una sola
        // reaplicación de 5 minutos filtraba ~150 conexiones. Agotado el pool, cualquier request
        // posterior se queda esperando una conexión libre hasta el timeout — el síntoma que se
        // veía como "el sistema está lento / se queda procesando".
        using IStorageConnection connection = storage.GetConnection();
        var jobData = connection.GetJobData(jobId);

        if (jobData == null)
        {
            return Results.NotFound(new { Message = "Job not found." });
        }

        return Results.Ok(new
        {
            JobId = jobId,
            State = jobData.State, // "Processing", "Succeeded", "Failed", etc.
            CreatedAt = jobData.CreatedAt
        });
    }
}
