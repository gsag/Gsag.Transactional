using Gsag.Transactional.Observability.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Gsag.Transactional.Tests.Samples.Observability;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddObservabilityPipeline_WithDefaultOptions_DoesNotRegisterHostedOpenTelemetryPipeline()
    {
        var services = new ServiceCollection();

        services.AddObservabilityPipeline();

        using var provider = services.BuildServiceProvider();
        Assert.Empty(provider.GetServices<IHostedService>());
    }

    [Fact]
    public void AddObservabilityPipeline_WhenTracingIsEnabled_RegistersHostedOpenTelemetryPipeline()
    {
        var services = new ServiceCollection();

        services.AddObservabilityPipeline(options => options.EnableTracing = true);

        using var provider = services.BuildServiceProvider();
        Assert.NotEmpty(provider.GetServices<IHostedService>());
    }

    [Fact]
    public void AddObservabilityPipeline_WhenMetricsAreEnabled_RegistersHostedOpenTelemetryPipeline()
    {
        var services = new ServiceCollection();

        services.AddObservabilityPipeline(options => options.EnableMetrics = true);

        using var provider = services.BuildServiceProvider();
        Assert.NotEmpty(provider.GetServices<IHostedService>());
    }

    [Fact]
    public void AddObservabilityPipeline_WithExplicitServiceMetadata_ConfiguresPipeline()
    {
        var services = new ServiceCollection();

        services.AddObservabilityPipeline(options =>
        {
            options.EnableTracing = true;
            options.EnableMetrics = true;
            options.ServiceName = "custom-service";
            options.ServiceVersion = "1.2.3";
        });

        using var provider = services.BuildServiceProvider();
        Assert.NotEmpty(provider.GetServices<IHostedService>());
    }
}