using System.Reflection;
using Gsag.Transactional.Core.Observability;

namespace Gsag.Transactional.Core.Extensions;

/// <summary>
/// Fluent builder for configuring transactional services.
/// Used with <see cref="TransactionalExtensions.AddTransactional(Microsoft.Extensions.DependencyInjection.IServiceCollection, System.Action{ITransactionalBuilder})"/>.
/// </summary>
public interface ITransactionalBuilder
{
    /// <summary>
    /// Scans <paramref name="assembly"/> for concrete service classes that have at least one
    /// [Transactional] method (on the class or on its implemented interfaces), then registers
    /// each one paired with its I{ClassName} interface as a TransactionProxy.
    /// Convention: OrderService → IOrderService (interface must be in the same namespace).
    ///
    /// IMPORTANT: Calling this method **overwrites** the default automatic calling-assembly discovery.
    /// Only the specified assembly is scanned. Call multiple times to scan additional assemblies.
    /// </summary>
    ITransactionalBuilder ScanAssembly(Assembly assembly);

    /// <summary>
    /// Registers the built-in logging observer. All proxied services emit structured log entries
    /// at DEBUG (BEGIN/COMMIT/COMPLETE) and WARNING (ROLLBACK) level under the category
    /// <c>Gsag.Transactional.Core.Observability.ITransactionObserver</c>.
    /// Idempotent — calling multiple times registers only one logging observer.
    /// </summary>
    ITransactionalBuilder AddLogging();

    /// <summary>
    /// Registers <typeparamref name="T"/> as an additional <see cref="ITransactionObserver"/>.
    /// When multiple observers are registered, the proxy wraps them in a
    /// <see cref="CompositeTransactionObserver"/> and calls each in registration order.
    /// <typeparamref name="T"/> is also registered as its concrete type so it can be injected
    /// directly (e.g., a metrics observer injected into a controller to read counters).
    /// Calling this method more than once with the same <typeparamref name="T"/> is idempotent.
    /// Observers must use Singleton or Transient lifetime — never Scoped.
    /// Can be chained to register multiple custom observers.
    /// </summary>
    ITransactionalBuilder AddObserver<T>() where T : class, ITransactionObserver;

    /// <summary>
    /// Registers a single transactional service pair without naming-convention constraints.
    /// Use when the interface is in a different assembly, has a non-standard name, or when
    /// explicit registration is preferred over assembly scanning.
    /// Can be chained to register multiple service pairs.
    /// </summary>
    ITransactionalBuilder AddService<TInterface, TImplementation>()
        where TInterface : class
        where TImplementation : class, TInterface;
}
