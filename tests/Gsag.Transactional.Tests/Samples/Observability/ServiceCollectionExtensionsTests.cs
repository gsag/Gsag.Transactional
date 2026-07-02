using Gsag.Transactional.Observability.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Gsag.Transactional.Tests.Samples.Observability;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddObservabilityPipeline_RegistersHostedOpenTelemetryPipeline()
    {
        var services = new ServiceCollection();

        services.AddObservabilityPipeline();

        using var provider = services.BuildServiceProvider();
        Assert.NotEmpty(provider.GetServices<IHostedService>());
    }
}
