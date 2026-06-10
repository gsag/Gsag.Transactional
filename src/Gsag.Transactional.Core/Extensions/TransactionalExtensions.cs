using System.Diagnostics.CodeAnalysis;
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
    /// Example:
    /// <code>
    /// builder.Services.AddTransactional(b => b
    ///     .ScanAssembly(typeof(MyService).Assembly)
    ///     .AddLogging()
    ///     .AddObserver&lt;MyMetricsObserver&gt;()
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
        var builder = new TransactionalBuilder(services);
        configure?.Invoke(builder);
        return services;
    }
}
