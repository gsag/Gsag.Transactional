using Gsag.Transactional.Observability.Extensions;
using Microsoft.Extensions.Configuration;
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
    public void AddObservabilityPipeline_WithConfigurationMissingObservabilitySection_DoesNotRegisterHostedOpenTelemetryPipeline()
    {
        var services = new ServiceCollection();
        var configuration = CreateConfiguration([]);

        services.AddObservabilityPipeline(configuration);

        using var provider = services.BuildServiceProvider();
        Assert.Empty(provider.GetServices<IHostedService>());
    }

    [Fact]
    public void AddObservabilityPipeline_WithConfigurationSection_BindsOptionsAndRegistersPipeline()
    {
        var services = new ServiceCollection();
        var configuration = CreateConfiguration(new Dictionary<string, string?>
        {
            ["Observability:EnableTracing"] = "true",
            ["Observability:EnableMetrics"] = "true",
            ["Observability:Traces:Endpoint"] = "http://localhost:4317",
            ["Observability:Metrics:Endpoint"] = "http://localhost:4317"
        });

        services.AddObservabilityPipeline(configuration);

        using var provider = services.BuildServiceProvider();
        Assert.NotEmpty(provider.GetServices<IHostedService>());
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

    [Fact]
    public void AddObservabilityPipeline_WithCustomGrpcOtlpEndpoints_ConfiguresPipeline()
    {
        var services = new ServiceCollection();

        services.AddObservabilityPipeline(options =>
        {
            options.EnableTracing = true;
            options.EnableMetrics = true;
            options.Traces.Endpoint = "http://localhost:4317";
            options.Metrics.Endpoint = "http://localhost:4317";
        });

        using var provider = services.BuildServiceProvider();
        Assert.NotEmpty(provider.GetServices<IHostedService>());
    }

    [Fact]
    public void AddObservabilityPipeline_WithInvalidDisabledSignalEndpoint_ConfiguresPipeline()
    {
        var services = new ServiceCollection();

        services.AddObservabilityPipeline(options =>
        {
            options.EnableTracing = true;
            options.Metrics.Endpoint = "not-a-uri";
        });

        using var provider = services.BuildServiceProvider();
        Assert.NotEmpty(provider.GetServices<IHostedService>());
    }

    [Fact]
    public void AddObservabilityPipeline_WithInvalidTraceEndpoint_ThrowsArgumentException()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<ArgumentException>(() =>
            services.AddObservabilityPipeline(options =>
            {
                options.EnableTracing = true;
                options.Traces.Endpoint = "not-a-uri";
            }));

        Assert.Equal("Traces.Endpoint", exception.ParamName);
    }

    [Fact]
    public void AddObservabilityPipeline_WithInvalidMetricsEndpoint_ThrowsArgumentException()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<ArgumentException>(() =>
            services.AddObservabilityPipeline(options =>
            {
                options.EnableMetrics = true;
                options.Metrics.Endpoint = "not-a-uri";
            }));

        Assert.Equal("Metrics.Endpoint", exception.ParamName);
    }

    private static IConfiguration CreateConfiguration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
}
