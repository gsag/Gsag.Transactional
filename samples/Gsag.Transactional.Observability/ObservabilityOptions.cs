using OpenTelemetry.Exporter;

namespace Gsag.Transactional.Observability;

/// <summary>
/// Configures optional observability features for transactional sample applications.
/// </summary>
public sealed class ObservabilityOptions
{
    /// <summary>
    /// Logical service name reported to observability providers. When not set, configuration
    /// can infer it from the entry assembly.
    /// </summary>
    public string? ServiceName { get; set; }

    /// <summary>
    /// Logical service version reported to observability providers. When not set, configuration
    /// can infer it from the entry assembly informational version.
    /// </summary>
    public string? ServiceVersion { get; set; }

    /// <summary>
    /// Enables trace instrumentation when the OpenTelemetry pipeline is configured.
    /// </summary>
    public bool EnableTracing { get; set; }

    /// <summary>
    /// Enables metric instrumentation when the OpenTelemetry pipeline is configured.
    /// </summary>
    public bool EnableMetrics { get; set; }

    /// <summary>
    /// Enables log export configuration when a logging pipeline is configured.
    /// </summary>
    public bool EnableLogs { get; set; }

    /// <summary>
    /// Trace provider configuration.
    /// </summary>
    public TraceProviderOptions Traces { get; set; } = new();

    /// <summary>
    /// Metrics provider configuration.
    /// </summary>
    public MetricsProviderOptions Metrics { get; set; } = new();

    /// <summary>
    /// Logs provider configuration.
    /// </summary>
    public LogsProviderOptions Logs { get; set; } = new();
}

/// <summary>
/// Provider-agnostic trace configuration.
/// </summary>
public sealed class TraceProviderOptions
{
    /// <summary>
    /// Trace export protocol used when tracing is enabled.
    /// </summary>
    public OtlpExportProtocol Protocol { get; set; } = OtlpExportProtocol.Grpc;

    /// <summary>
    /// Trace export endpoint used when tracing is enabled.
    /// </summary>
    public string Endpoint { get; set; } = "http://localhost:4317";
}

/// <summary>
/// Provider-agnostic metrics configuration.
/// </summary>
public sealed class MetricsProviderOptions
{
    /// <summary>
    /// Metrics export protocol used when metrics are enabled.
    /// </summary>
    public OtlpExportProtocol Protocol { get; set; } = OtlpExportProtocol.Grpc;

    /// <summary>
    /// Metrics export endpoint used when metrics are enabled.
    /// </summary>
    public string Endpoint { get; set; } = "http://localhost:4317";
}

/// <summary>
/// Provider-agnostic logs configuration.
/// </summary>
public sealed class LogsProviderOptions
{
    /// <summary>
    /// Logs export endpoint used when logs are enabled.
    /// </summary>
    public string Endpoint { get; set; } = "http://localhost:3100";
}
