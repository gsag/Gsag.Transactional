using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

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
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddObservabilityHealthChecks(configuration);
        services.AddSingleton<IStartupFilter, ObservabilityStartupFilter>();

        return services.AddObservabilityPipeline(
            configuration.GetSection(OpenTelemetryConventions.Configuration.SectionName));
    }

    /// <summary>
    /// Adds the OpenTelemetry pipeline that listens to transactional observability instruments.
    /// </summary>
    public static IServiceCollection AddObservabilityPipeline(
        this IServiceCollection services,
        IConfigurationSection configurationSection)
    {
        ArgumentNullException.ThrowIfNull(configurationSection);

        return services.AddObservabilityPipeline(configurationSection.Bind);
    }

    /// <summary>
    /// Adds the OpenTelemetry pipeline that listens to transactional observability instruments.
    /// </summary>
    public static IServiceCollection AddObservabilityPipeline(
        this IServiceCollection services,
        Action<ObservabilityOptions>? configure)
    {
        var options = new ObservabilityOptions();
        configure?.Invoke(options);

        if (!options.EnableTracing && !options.EnableMetrics && !options.EnableLogs)
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
            var tracesEndpoint = CreateEndpoint(
                options.Traces.Endpoint,
                OpenTelemetryConventions.Configuration.TracesEndpoint);

            builder.WithTracing(tracing => tracing
                .SetSampler(new AlwaysOnSampler())
                .SetResourceBuilder(resourceBuilder)
                .AddSource(OpenTelemetryConventions.InstrumentationName)
                .AddOtlpExporter(exporter =>
                {
                    exporter.Protocol = options.Traces.Protocol;
                    exporter.Endpoint = tracesEndpoint;
                }));
        }

        if (options.EnableMetrics)
        {
            var metricsEndpoint = CreateEndpoint(
                options.Metrics.Endpoint,
                OpenTelemetryConventions.Configuration.MetricsEndpoint);

            builder.WithMetrics(metrics => metrics
                .SetResourceBuilder(resourceBuilder)
                .AddMeter(OpenTelemetryConventions.InstrumentationName)
                .AddOtlpExporter(exporter =>
                {
                    exporter.Protocol = options.Metrics.Protocol;
                    exporter.Endpoint = metricsEndpoint;
                }));
        }

        if (options.EnableLogs)
        {
            var logsEndpoint = CreateEndpoint(
                options.Logs.Endpoint,
                OpenTelemetryConventions.Configuration.LogsEndpoint);

            services.AddLogging(logging => logging.AddSerilog(dispose: true));

            builder.WithLogging(logging => logging
                .AddOtlpExporter(exporter =>
                {
                    exporter.Protocol = options.Logs.Protocol;
                    exporter.Endpoint = logsEndpoint;
                }));
        }

        return services;
    }

    private static Uri CreateEndpoint(string endpoint, string optionName)
    {
        if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            return uri;
        }

        throw new ArgumentException(
            $"Observability option '{optionName}' must be an absolute URI.",
            optionName);
    }
}
