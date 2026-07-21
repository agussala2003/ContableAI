using System.Net.Mail;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using Polly.Timeout;

namespace ContableAI.Infrastructure.Resilience;

/// <summary>
/// Fábrica central de pipelines de resiliencia (Polly v8), registrados por nombre en el contenedor
/// de DI y recuperables vía <c>ResiliencePipelineProvider&lt;string&gt;</c>.
///
/// Hoy define el pipeline de envío de email (SMTP/Resend). El día de mañana, al integrar pasarelas
/// de pago u otras APIs HTTP, se agregan pipelines nuevos acá — o un <c>AddResilienceHandler</c>
/// para <c>HttpClient</c> de la misma familia (<c>Microsoft.Extensions.Http.Resilience</c>) — sin
/// dispersar la política de resiliencia por el código.
/// </summary>
public static class ResiliencePipelines
{
    /// <summary>Nombre del pipeline de envío de email (SMTP a través de Resend).</summary>
    public const string Email = "email";

    // ── Parámetros del pipeline de email ────────────────────────────────────────
    private const int    EmailMaxRetries       = 3;
    private static readonly TimeSpan EmailBaseDelay      = TimeSpan.FromSeconds(1);  // backoff exponencial: ~1s, 2s, 4s (+ jitter)
    private static readonly TimeSpan EmailAttemptTimeout = TimeSpan.FromSeconds(10); // corte por intento
    private static readonly TimeSpan EmailTotalTimeout   = TimeSpan.FromSeconds(30); // cota absoluta de toda la secuencia

    /// <summary>Registra los pipelines de resiliencia de la aplicación.</summary>
    public static IServiceCollection AddContableResilience(this IServiceCollection services)
    {
        services.AddResiliencePipeline(Email, (builder, context) =>
        {
            var logger = context.ServiceProvider
                .GetService<ILoggerFactory>()?
                .CreateLogger("Resilience.Email");

            builder
                // 1) Timeout absoluto (externo): cota total de toda la secuencia de reintentos, para
                //    que el hilo nunca quede bloqueado indefinidamente esperando a Resend.
                .AddTimeout(EmailTotalTimeout)

                // 2) Reintentos con backoff exponencial + jitter ante fallos transitorios de red o
                //    del servidor SMTP (incluido el timeout por intento del paso 3).
                .AddRetry(new RetryStrategyOptions
                {
                    ShouldHandle = new PredicateBuilder()
                        .Handle<SmtpException>()
                        .Handle<IOException>()
                        .Handle<SocketException>()
                        .Handle<TimeoutRejectedException>(),
                    MaxRetryAttempts = EmailMaxRetries,
                    BackoffType      = DelayBackoffType.Exponential,
                    Delay            = EmailBaseDelay,
                    UseJitter        = true,
                    OnRetry          = args =>
                    {
                        logger?.LogWarning(
                            "Reintento {Attempt}/{Max} del envío de email tras fallo transitorio: {Error}",
                            args.AttemptNumber + 1, EmailMaxRetries, args.Outcome.Exception?.Message);
                        return default;
                    },
                })

                // 3) Timeout por intento (interno): cada intento individual de SendMailAsync se corta
                //    a los 10s y dispara un reintento (el paso 2 maneja TimeoutRejectedException).
                .AddTimeout(EmailAttemptTimeout);
        });

        return services;
    }
}
