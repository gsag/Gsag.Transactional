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
                .FirstOrDefault(i =>
                    i.Name == $"I{serviceType.Name}"
                    && i.Assembly == assembly
                    && i.Namespace == serviceType.Namespace);

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
                var registeredObservers = sp.GetServices<ITransactionLifecycleObserver>().ToList();
                ITransactionLifecycleObserver observer = registeredObservers.Count switch
                {
                    0 => NullTransactionObserver.Instance,
                    1 => registeredObservers[0],
                    _ => new CompositeTransactionObserver(registeredObservers)
                };
                return TransactionProxyFactory.Create(interfaceType, instance, observer);
            });
        }

        return services;
    }

    /// <summary>
    /// Registers <see cref="LoggingTransactionObserver"/> as a transaction lifecycle observer.
    /// All proxied services will emit structured log entries at DEBUG (BEGIN/COMMIT/COMPLETE)
    /// and WARNING (ROLLBACK) level. Can be combined with other observers via
    /// <see cref="AddTransactionalObserver{T}"/> — the proxy will dispatch to all in registration order.
    /// </summary>
    public static IServiceCollection AddTransactionalLogging(this IServiceCollection services) =>
        services.AddTransactionalObserver<LoggingTransactionObserver>();

    /// <summary>
    /// Registers <typeparamref name="T"/> as an additional <see cref="ITransactionLifecycleObserver"/>.
    /// When multiple observers are registered, the proxy wraps them in a
    /// <see cref="CompositeTransactionObserver"/> and calls each in registration order.
    /// <typeparamref name="T"/> is also registered as its concrete type so it can be injected
    /// directly (e.g., a metrics observer injected into a controller to read counters).
    /// Calling this method more than once with the same <typeparamref name="T"/> is idempotent.
    /// Observers must use Singleton or Transient lifetime — never Scoped.
    /// </summary>
    public static IServiceCollection AddTransactionalObserver<T>(this IServiceCollection services)
        where T : class, ITransactionLifecycleObserver
    {
        // Guard type prevents duplicate forwarding when this method is called more than once
        // with the same T (e.g., AddTransactionalLogging() called twice).
        if (services.Any(d => d.ServiceType == typeof(ObserverRegistered<T>)))
        {
            return services;
        }
        services.AddSingleton<ObserverRegistered<T>>();
        services.TryAddSingleton<T>();
        // Forward ITransactionLifecycleObserver to the concrete singleton so the same
        // instance is used whether resolved via the interface or the concrete type.
        services.AddSingleton<ITransactionLifecycleObserver>(sp => sp.GetRequiredService<T>());
        return services;
    }

    // Internal marker: one instance per observer type T, used to detect duplicate
    // AddTransactionalObserver<T>() calls and prevent double registration.
    private sealed class ObserverRegistered<T> where T : class, ITransactionLifecycleObserver { }

    // BindingFlags.NonPublic is included to detect explicitly implemented interface methods,
    // which are Private from the declaring class's perspective.
    private static bool HasTransactionalMethod(Type type) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Any(m => m.IsDefined(typeof(TransactionalAttribute), inherit: true))
        || type.GetInterfaces()
            .SelectMany(i => i.GetMethods())
            .Any(m => m.IsDefined(typeof(TransactionalAttribute), inherit: true));
}
