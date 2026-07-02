using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
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
    public static IServiceCollection AddObservabilityPipeline(this IServiceCollection services)
    {
        services.AddOpenTelemetry()
            .WithTracing(tracing => tracing
                .AddSource(OpenTelemetryConventions.InstrumentationName))
            .WithMetrics(metrics => metrics
                .AddMeter(OpenTelemetryConventions.InstrumentationName));

        return services;
    }
}
