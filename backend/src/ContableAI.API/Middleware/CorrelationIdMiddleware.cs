using System.Diagnostics;
using System.Text.RegularExpressions;
using Serilog.Context;

namespace ContableAI.API.Middleware;

/// <summary>
/// O-1: Correlation ID de punta a punta.
///
/// Lee el header <c>X-Correlation-Id</c> entrante (o genera uno nuevo), lo publica en:
///   • <see cref="Serilog.Context.LogContext"/>  → todos los logs del request lo llevan como propiedad estructurada.
///   • <see cref="Activity"/> (baggage)          → viaja por la traza distribuida a los child activities.
///   • <see cref="CorrelationIdContext"/>         → contexto ambiente que el filtro de Hangfire lee al encolar un job.
///   • <see cref="HttpContext.Items"/>            → disponible para el <c>GlobalExceptionHandler</c> (traceId en ProblemDetails).
///   • Header de respuesta <c>X-Correlation-Id</c> → el cliente puede citarlo al reportar un error.
///
/// Debe registrarse temprano en el pipeline (tras UseForwardedHeaders, antes de
/// UseExceptionHandler y UseSerilogRequestLogging) para que el ID exista en el log
/// de finalización del request y en el manejo global de excepciones.
/// </summary>
public sealed class CorrelationIdMiddleware
{
    public const string HeaderName  = "X-Correlation-Id";
    public const string LogProperty = "CorrelationId";
    public const string ItemsKey    = "CorrelationId";

    // El ID entrante es input no confiable: se acota longitud y charset para evitar
    // log-forging (inyección de saltos de línea) y valores abusivamente largos.
    private const int MaxLength = 128;
    private static readonly Regex SafePattern = new(@"^[A-Za-z0-9\-_.]{1,128}$", RegexOptions.Compiled);

    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = ResolveCorrelationId(context);

        context.Items[ItemsKey]      = correlationId;
        CorrelationIdContext.Current = correlationId;
        Activity.Current?.SetBaggage(LogProperty, correlationId);

        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (LogContext.PushProperty(LogProperty, correlationId))
        {
            await _next(context);
        }
    }

    private static string ResolveCorrelationId(HttpContext context)
    {
        var incoming = context.Request.Headers[HeaderName].ToString();
        if (!string.IsNullOrWhiteSpace(incoming))
        {
            incoming = incoming.Trim();
            if (incoming.Length <= MaxLength && SafePattern.IsMatch(incoming))
                return incoming;
        }

        // Sin header válido: preferir el TraceId de la traza en curso; si no hay Activity,
        // generar un GUID compacto.
        return Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("n");
    }
}

/// <summary>
/// Contexto ambiente (<see cref="AsyncLocal{T}"/>) del Correlation ID en curso. Fluye por
/// la cadena async del request, de modo que cuando un handler encola un job de Hangfire, el
/// <c>HangfireCorrelationIdFilter</c> puede leer el ID vigente sin depender del HttpContext.
/// </summary>
public static class CorrelationIdContext
{
    private static readonly AsyncLocal<string?> _current = new();

    public static string? Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }
}
