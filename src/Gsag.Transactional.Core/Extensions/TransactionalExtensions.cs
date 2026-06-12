using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Gsag.Transactional.Core.Extensions;

/// <summary>
/// Extension methods for registering Gsag.Transactional.Core services with the .NET DI container.
/// </summary>
public static class TransactionalExtensions
{
    /// <summary>
    /// Registers transactional services using a fluent builder.
    ///
    /// Automatically scans the calling assembly for service classes with [Transactional] methods.
    /// To scan additional assemblies instead, use <c>.ScanAssembly()</c>.
    ///
    /// Example:
    /// <code>
    /// builder.Services.AddTransactional(b => b
    ///     .AddLogging()
    ///     .AddObserver&lt;MyMetricsObserver&gt;()
    ///     .ScanAssembly(typeof(SomeOtherAssembly).Assembly)  // Optional: scan additional assemblies
    /// );
    /// </code>
    ///
    /// The builder is optional — calling without a callback registers the bare hooks but no services or observers:
    /// <code>builder.Services.AddTransactional();</code>
    /// </summary>
    [RequiresUnreferencedCode(
        "ScanAssembly reflects to find [Transactional] methods. " +
        "Ensure all service types and their interface members are preserved when publishing with trimming.")]
    public static IServiceCollection AddTransactional(
        this IServiceCollection services,
        Action<ITransactionalBuilder>? configure = null)
    {
        var callingAssembly = Assembly.GetCallingAssembly();
        var builder = new TransactionalBuilder(services, callingAssembly);
        configure?.Invoke(builder);
        builder.EnsureDiscoveryRegistered();
        return services;
    }
}
