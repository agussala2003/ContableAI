using Hangfire;
using Microsoft.AspNetCore.Mvc;

namespace ContableAI.API.Endpoints;

public static class JobsEndpoints
{
    public static void MapJobsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/jobs/{jobId}/status", (string jobId) =>
        {
            var connection = JobStorage.Current.GetConnection();
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
        })
        .WithName("GetJobStatus")
        .WithTags("Jobs")
        .WithSummary("Obtener el estado de un trabajo en segundo plano (Hangfire).")
        .Produces(200)
        .Produces(404);
    }
}
