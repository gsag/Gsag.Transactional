using System.Reflection;

namespace Gsag.Transactional.Observability;

internal sealed record ObservabilityServiceMetadata(string ServiceName, string? ServiceVersion);

internal static class ObservabilityServiceMetadataResolver
{
    internal static ObservabilityServiceMetadata Resolve(ObservabilityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var entryAssembly = Assembly.GetEntryAssembly();
        return new ObservabilityServiceMetadata(
            ResolveServiceName(options, entryAssembly),
            ResolveServiceVersion(options, entryAssembly));
    }

    private static string ResolveServiceName(ObservabilityOptions options, Assembly? entryAssembly) =>
        FirstNonEmpty(options.ServiceName, entryAssembly?.GetName().Name)
        ?? OpenTelemetryConventions.InstrumentationName;

    private static string? ResolveServiceVersion(ObservabilityOptions options, Assembly? entryAssembly) =>
        FirstNonEmpty(
            options.ServiceVersion,
            entryAssembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
