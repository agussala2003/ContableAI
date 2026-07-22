using ContableAI.Application.Features.Transactions.Commands;
using Hangfire.States;

namespace ContableAI.API.Common;

/// <summary>Nombres de las colas de Hangfire (P-9). Centralizados para que servers y filtro no diverjan.</summary>
public static class HangfireQueues
{
    public const string Default = "default";

    /// <summary>Procesamiento de extractos (parseo/OCR): CPU-bound y de larga duración.</summary>
    public const string Uploads = "uploads";
}

/// <summary>
/// P-9: enruta jobs a su cola según el command de MediatR que transportan. Todos los jobs se
/// encolan con el mismo patrón (<c>Enqueue&lt;ISender&gt;(s =&gt; s.Send(command))</c>), así que la
/// cola no puede fijarse con el atributo <c>[Queue]</c> por método — se decide acá inspeccionando
/// los argumentos del job. Corre también en reintentos (cada re-elección de EnqueuedState),
/// por lo que un retry de subida vuelve a "uploads" y no a "default".
/// </summary>
public sealed class HangfireQueueRoutingFilter : IElectStateFilter
{
    public void OnStateElection(ElectStateContext context)
    {
        if (context.CandidateState is EnqueuedState enqueued
            && context.BackgroundJob.Job.Args.Any(a => a is UploadBankStatementCommand))
        {
            enqueued.Queue = HangfireQueues.Uploads;
        }
    }
}
