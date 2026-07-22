using ContableAI.Infrastructure.Resilience;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Polly.Registry;

namespace ContableAI.Tests.Infrastructure;

/// <summary>
/// Verifica el cableado del pipeline de resiliencia del email: que <c>AddContableResilience</c> lo
/// registre y que sea recuperable por el mismo nombre (<see cref="ResiliencePipelines.Email"/>) que
/// usa <c>SmtpEmailService</c> en su constructor. Es una guarda contra que el registro se rompa o el
/// nombre del pipeline diverja del que consume el servicio. No ejecuta envíos ni incurre en los
/// delays del backoff (el comportamiento de retry/timeout de Polly ya está testeado por Polly).
/// </summary>
public class EmailResilienceTests
{
    [Fact]
    public void EmailPipeline_IsRegistered_AndRetrievableByServiceName()
    {
        var services = new ServiceCollection();
        services.AddContableResilience();
        using var provider = services.BuildServiceProvider();

        var pipelines = provider.GetRequiredService<ResiliencePipelineProvider<string>>();

        var getEmailPipeline = () => pipelines.GetPipeline(ResiliencePipelines.Email);

        getEmailPipeline.Should().NotThrow(
            "SmtpEmailService resuelve el pipeline de email por este nombre en su constructor");
        getEmailPipeline().Should().NotBeNull();
    }
}
