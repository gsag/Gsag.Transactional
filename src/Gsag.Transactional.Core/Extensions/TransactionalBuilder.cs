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
/// Internal implementation of fluent transactional service configuration.
/// </summary>
internal sealed class TransactionalBuilder : ITransactionalBuilder
{
    private readonly IServiceCollection _services;

    internal TransactionalBuilder(IServiceCollection services)
    {
        _services = services;
    }

    public ITransactionalBuilder ScanAssembly(Assembly assembly)
    {
        _services.TryAddSingleton<ITransactionHooks, TransactionHooks>();

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

            _services.AddScoped(serviceType);

            _services.AddScoped(interfaceType, sp =>
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

        return this;
    }

    public ITransactionalBuilder AddLogging()
    {
        return AddObserver<LoggingTransactionObserver>();
    }

    public ITransactionalBuilder AddObserver<T>() where T : class, ITransactionObserver
    {
        if (_services.Any(d => d.ServiceType == typeof(ObserverRegistered<T>)))
        {
            return this;
        }
        _services.AddSingleton<ObserverRegistered<T>>();
        _services.TryAddSingleton<T>();
        _services.AddSingleton<ITransactionObserver>(sp => sp.GetRequiredService<T>());
        return this;
    }

    public ITransactionalBuilder AddService<TInterface, TImplementation>()
        where TInterface : class
        where TImplementation : class, TInterface
    {
        _services.TryAddSingleton<ITransactionHooks, TransactionHooks>();
        _services.AddScoped<TImplementation>();
        _services.AddScoped<TInterface>(sp =>
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
        return this;
    }

    [RequiresUnreferencedCode("Inspects arbitrary types for [Transactional] attributes via reflection.")]
    [SuppressMessage("Vulnerability", "S3011", Justification = "Reflects on user-supplied types to discover [Transactional] methods; this is the intended purpose of the reflection.")]
    private static bool HasTransactionalMethod(Type type) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Any(m => m.IsDefined(typeof(TransactionalAttribute), inherit: true))
        || type.GetInterfaces()
            .SelectMany(i => i.GetMethods())
            .Any(m => m.IsDefined(typeof(TransactionalAttribute), inherit: true));
}

[SuppressMessage("Major Code Smell", "S2094", Justification = "Intentional marker class — emptiness is the point; the type's existence in the DI container is the signal.")]
[SuppressMessage("Major Code Smell", "S2326", Justification = "T is the discriminator that makes each closed generic a unique DI marker; it is not used in the body by design.")]
internal sealed class ObserverRegistered<T> where T : class, ITransactionObserver { }
