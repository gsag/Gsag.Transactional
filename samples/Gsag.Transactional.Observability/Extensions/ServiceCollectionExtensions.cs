using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Gsag.Transactional.Observability.Extensions;

/// <summary>
/// Extension methods for configuring the observability pipeline.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the OpenTelemetry pipeline that listens to transactional observability instruments.
    /// </summary>
    public static IServiceCollection AddObservabilityPipeline(this IServiceCollection services) =>
        services.AddObservabilityPipeline(configure: null);

    /// <summary>
    /// Adds the OpenTelemetry pipeline that listens to transactional observability instruments.
    /// </summary>
    public static IServiceCollection AddObservabilityPipeline(
        this IServiceCollection services,
        Action<ObservabilityOptions>? configure)
    {
        var options = new ObservabilityOptions();
        configure?.Invoke(options);

        if (!options.EnableTracing && !options.EnableMetrics)
        {
            return services;
        }

        var metadata = ObservabilityServiceMetadataResolver.Resolve(options);
        var resourceBuilder = ResourceBuilder.CreateDefault()
            .AddService(
                serviceName: metadata.ServiceName,
                serviceVersion: metadata.ServiceVersion);

        var builder = services.AddOpenTelemetry();

        if (options.EnableTracing)
        {
            builder.WithTracing(tracing => tracing
                .SetResourceBuilder(resourceBuilder)
                .AddSource(OpenTelemetryConventions.InstrumentationName));
        }

        if (options.EnableMetrics)
        {
            builder.WithMetrics(metrics => metrics
                .SetResourceBuilder(resourceBuilder)
                .AddMeter(OpenTelemetryConventions.InstrumentationName));
        }

        return services;
    }
}