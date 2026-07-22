using ContableAI.API.Middleware;
using Hangfire.Client;
using Hangfire.Server;
using Serilog.Context;

namespace ContableAI.API.Common;

/// <summary>
/// O-1 (tramo Hangfire): propaga el Correlation ID desde el request HTTP que encola un job
/// hasta la ejecución diferida del job en el worker.
///
///   • <see cref="OnCreating"/> (cliente): al encolar, captura el Correlation ID ambiente
///     (<see cref="CorrelationIdContext.Current"/>) y lo persiste como parámetro del job.
///   • <see cref="OnPerforming"/> (servidor): al ejecutar, restaura ese ID al
///     <see cref="LogContext"/> del worker; si el job no trae ninguno (p. ej. un recurring
///     disparado por el scheduler, sin request de origen) usa el propio Id del job como traza.
/// </summary>
public sealed class HangfireCorrelationIdFilter : IClientFilter, IServerFilter
{
    private const string ParameterName = "CorrelationId";
    private const string ScopeItemKey  = "CorrelationIdScope";

    // ── Cliente ───────────────────────────────────────────────────────────────
    public void OnCreating(CreatingContext context)
    {
        var correlationId = CorrelationIdContext.Current;
        if (!string.IsNullOrWhiteSpace(correlationId))
            context.SetJobParameter(ParameterName, correlationId);
    }

    public void OnCreated(CreatedContext context) { }

    // ── Servidor ──────────────────────────────────────────────────────────────
    public void OnPerforming(PerformingContext context)
    {
        var correlationId = context.GetJobParameter<string>(ParameterName);
        if (string.IsNullOrWhiteSpace(correlationId))
            correlationId = $"job:{context.BackgroundJob.Id}";

        CorrelationIdContext.Current = correlationId;
        context.Items[ScopeItemKey]  = LogContext.PushProperty(CorrelationIdMiddleware.LogProperty, correlationId);
    }

    public void OnPerformed(PerformedContext context)
    {
        if (context.Items.TryGetValue(ScopeItemKey, out var scope) && scope is IDisposable disposable)
            disposable.Dispose();
    }
}
