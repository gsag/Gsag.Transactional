using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Gsag.Transactional.Core.Attributes;
using Gsag.Transactional.Core.Hooks;
using Gsag.Transactional.Core.Observability;
using Gsag.Transactional.Core.Proxy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Gsag.Transactional.Core.Extensions;

/// <summary>
/// Extension methods for registering Gsag.Transactional.Core services with the .NET DI container.
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
    [RequiresUnreferencedCode(
        "Scans the assembly for [Transactional] methods using reflection. " +
        "Ensure all service types and their interface members are preserved when publishing with trimming.")]
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
                var registeredObservers = sp.GetServices<ITransactionObserver>().ToList();
                ITransactionObserver observer = registeredObservers.Count switch
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
    /// Registers the built-in logging observer. All proxied services emit structured log entries
    /// at DEBUG (BEGIN/COMMIT/COMPLETE) and WARNING (ROLLBACK) level under the category
    /// <c>Gsag.Transactional.Core.Observability.ITransactionObserver</c>.
    /// To filter or silence these entries, configure that category in appsettings.json.
    /// To change the format, skip this method and register your own
    /// <see cref="ITransactionObserver"/> via <see cref="AddTransactionalObserver{T}"/> instead.
    /// Can be combined with other observers — the proxy dispatches to all in registration order.
    /// </summary>
    public static IServiceCollection AddTransactionalLogging(this IServiceCollection services) =>
        services.AddTransactionalObserver<LoggingTransactionObserver>();

    /// <summary>
    /// Registers <typeparamref name="T"/> as an additional <see cref="ITransactionObserver"/>.
    /// When multiple observers are registered, the proxy wraps them in a
    /// <see cref="CompositeTransactionObserver"/> and calls each in registration order.
    /// <typeparamref name="T"/> is also registered as its concrete type so it can be injected
    /// directly (e.g., a metrics observer injected into a controller to read counters).
    /// Calling this method more than once with the same <typeparamref name="T"/> is idempotent.
    /// Observers must use Singleton or Transient lifetime — never Scoped.
    /// </summary>
    public static IServiceCollection AddTransactionalObserver<T>(this IServiceCollection services)
        where T : class, ITransactionObserver
    {
        // Guard type prevents duplicate forwarding when this method is called more than once
        // with the same T (e.g., AddTransactionalLogging() called twice).
        if (services.Any(d => d.ServiceType == typeof(ObserverRegistered<T>)))
        {
            return services;
        }
        services.AddSingleton<ObserverRegistered<T>>();
        services.TryAddSingleton<T>();
        // Forward ITransactionObserver to the concrete singleton so the same
        // instance is used whether resolved via the interface or the concrete type.
        services.AddSingleton<ITransactionObserver>(sp => sp.GetRequiredService<T>());
        return services;
    }

    /// <summary>
    /// Registers a single transactional service pair without naming-convention constraints.
    /// Use when the interface is in a different assembly, has a non-standard name, or when
    /// explicit registration is preferred over assembly scanning.
    /// </summary>
    public static IServiceCollection AddTransactionalService<TInterface, TImplementation>(
        this IServiceCollection services)
        where TInterface : class
        where TImplementation : class, TInterface
    {
        services.TryAddSingleton<ITransactionHooks, TransactionHooks>();
        services.AddScoped<TImplementation>();
        services.AddScoped<TInterface>(sp =>
        {
            var instance = sp.GetRequiredService<TImplementation>();
            var registeredObservers = sp.GetServices<ITransactionObserver>().ToList();
            ITransactionObserver observer = registeredObservers.Count switch
            {
                0 => NullTransactionObserver.Instance,
                1 => registeredObservers[0],
                _ => new CompositeTransactionObserver(registeredObservers)
            };
            return TransactionProxyFactory.Create<TInterface>(instance, observer);
        });
        return services;
    }

    // Internal marker: one instance per observer type T, used to detect duplicate
    // AddTransactionalObserver<T>() calls and prevent double registration.
    private sealed class ObserverRegistered<T> where T : class, ITransactionObserver { }

    // BindingFlags.NonPublic is included to detect explicitly implemented interface methods,
    // which are Private from the declaring class's perspective.
    [RequiresUnreferencedCode("Inspects arbitrary types for [Transactional] attributes via reflection.")]
    private static bool HasTransactionalMethod(Type type) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Any(m => m.IsDefined(typeof(TransactionalAttribute), inherit: true))
        || type.GetInterfaces()
            .SelectMany(i => i.GetMethods())
            .Any(m => m.IsDefined(typeof(TransactionalAttribute), inherit: true));
}
