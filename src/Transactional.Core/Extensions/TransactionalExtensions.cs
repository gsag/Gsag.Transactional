using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Transactional.Core.Attributes;
using Transactional.Core.Hooks;
using Transactional.Core.Observability;
using Transactional.Core.Proxy;

namespace Transactional.Core.Extensions;

/// <summary>
/// Extension methods for registering Transactional.Core services with the .NET DI container.
/// </summary>
public static class TransactionalExtensions
{
    /// <summary>
    /// Scans <paramref name="assembly"/> for concrete service classes that have at least one
    /// [Transactional] method (on the class or on its implemented interfaces), then registers
    /// each one paired with its I{ClassName} interface as a TransactionProxy.
    ///
    /// Convention: OrderService → IOrderService (interface must be in the same assembly).
    /// </summary>
    public static IServiceCollection AddTransactionalServices(
        this IServiceCollection services, Assembly assembly)
    {
        // Singleton: TransactionHooks carries no per-instance state — _current is static AsyncLocal.
        services.TryAddSingleton<ITransactionHooks, TransactionHooks>();

        var candidates = assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && HasTransactionalMethod(t));

        foreach (var serviceType in candidates)
        {
            var interfaceType = serviceType.GetInterfaces()
                .FirstOrDefault(i => i.Name == $"I{serviceType.Name}" && i.Assembly == assembly);

            if (interfaceType is null)
            {
                continue;
            }

            // Register the concrete type so the DI container owns its lifetime and can
            // resolve any new constructor dependencies automatically.
            services.AddScoped(serviceType);

            services.AddScoped(interfaceType, sp =>
            {
                var instance = sp.GetRequiredService(serviceType);
                var observer = sp.GetService<ITransactionLifecycleObserver>();
                return TransactionProxyFactory.Create(interfaceType, instance, observer);
            });
        }

        return services;
    }

    /// <summary>
    /// Registers <see cref="LoggingTransactionObserver"/> as the ambient
    /// <see cref="ITransactionLifecycleObserver"/>. All proxied services will emit
    /// structured log entries at DEBUG (BEGIN/COMMIT) and WARNING (ROLLBACK) level.
    ///
    /// Custom implementations must use Singleton or Transient lifetime — never Scoped,
    /// since the observer is resolved once and shared across all proxy instances.
    /// </summary>
    public static IServiceCollection AddTransactionalLogging(this IServiceCollection services)
    {
        services.TryAddSingleton<ITransactionLifecycleObserver, LoggingTransactionObserver>();
        return services;
    }

    // BindingFlags.NonPublic is included to detect explicitly implemented interface methods,
    // which are Private from the declaring class's perspective.
    private static bool HasTransactionalMethod(Type type) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Any(m => m.IsDefined(typeof(TransactionalAttribute), inherit: true))
        || type.GetInterfaces()
            .SelectMany(i => i.GetMethods())
            .Any(m => m.IsDefined(typeof(TransactionalAttribute), inherit: true));
}
