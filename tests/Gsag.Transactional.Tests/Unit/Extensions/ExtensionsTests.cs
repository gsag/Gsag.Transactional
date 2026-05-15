using System.Reflection;
using Gsag.Transactional.Core.Attributes;
using Gsag.Transactional.Core.Extensions;
using Gsag.Transactional.Core.Hooks;
using Gsag.Transactional.Core.Observability;
using Gsag.Transactional.Tests.Unit;
using Gsag.Transactional.Tests.Unit.Extensions.Other;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gsag.Transactional.Tests.Unit.Extensions;

// Service pair that follows the I{ClassName} convention — discovered by AddTransactionalServices.
public interface IExtTestService { string Do(); }
public class ExtTestService : IExtTestService
{
    [Transactional]
    public string Do() => "done";
}

// Class with [Transactional] but no matching interface — must be skipped.
public class ExtTestOrphan
{
    [Transactional]
    public void Run() { }
}

// Service where [Transactional] is placed only on the interface, not the concrete class.
public interface IInterfaceOnlyAttrService
{
    [Transactional]
    string Run();
}
public class InterfaceOnlyAttrService : IInterfaceOnlyAttrService
{
    public string Run() => "ok";
}

// Service registered via the explicit AddTransactionalService<TInterface, TImplementation> overload.
// Uses a non-standard interface name to bypass the I{ClassName} convention.
public interface IManualService { string Do(); }
public class ManualServiceImpl : IManualService
{
    [Transactional]
    public string Do() => "manual";
}

public class ExtensionsTests
{
    private static ServiceCollection NewServices() => new();

    // -------------------------------------------------------------------------
    // AddTransactionalServices
    // -------------------------------------------------------------------------

    [Fact]
    public void AddTransactionalServices_RegistersInterfaceAsProxy()
    {
        var provider = NewServices()
            .AddTransactionalServices(Assembly.GetExecutingAssembly())
            .BuildServiceProvider();

        var svc = provider.GetRequiredService<IExtTestService>();

        Assert.IsAssignableFrom<IExtTestService>(svc);
        Assert.IsNotType<ExtTestService>(svc); // must be proxy, not concrete
    }

    [Fact]
    public void AddTransactionalServices_ProxiedService_IsScoped()
    {
        var services = NewServices()
            .AddTransactionalServices(Assembly.GetExecutingAssembly());

        var descriptor = services.Single(d => d.ServiceType == typeof(IExtTestService));

        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    [Fact]
    public void AddTransactionalServices_RegistersITransactionHooksAsSingleton()
    {
        var services = NewServices()
            .AddTransactionalServices(Assembly.GetExecutingAssembly());

        var descriptor = services.Single(d => d.ServiceType == typeof(ITransactionHooks));

        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    [Fact]
    public void AddTransactionalServices_ITransactionHooks_ReturnsSameInstance()
    {
        var provider = NewServices()
            .AddTransactionalServices(Assembly.GetExecutingAssembly())
            .BuildServiceProvider();

        var h1 = provider.GetRequiredService<ITransactionHooks>();
        var h2 = provider.GetRequiredService<ITransactionHooks>();

        Assert.Same(h1, h2);
    }

    [Fact]
    public void AddTransactionalServices_CalledTwice_DoesNotDuplicateHooksRegistration()
    {
        var services = NewServices()
            .AddTransactionalServices(Assembly.GetExecutingAssembly())
            .AddTransactionalServices(Assembly.GetExecutingAssembly());

        var count = services.Count(d => d.ServiceType == typeof(ITransactionHooks));

        Assert.Equal(1, count);
    }

    [Fact]
    public void AddTransactionalServices_ClassWithoutMatchingInterface_NotRegistered()
    {
        var services = NewServices()
            .AddTransactionalServices(Assembly.GetExecutingAssembly());

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(ExtTestOrphan));
    }

    // -------------------------------------------------------------------------
    // AddTransactionalLogging
    // -------------------------------------------------------------------------

    [Fact]
    public void AddTransactionalLogging_RegistersLoggingObserverAsSingleton()
    {
        var services = NewServices()
            .AddTransactionalLogging();

        // ITransactionObserver is registered via factory (forwarding to LoggingTransactionObserver).
        var descriptor = services.Single(d => d.ServiceType == typeof(ITransactionObserver));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);

        // Concrete type is also registered so it can be injected directly.
        Assert.Contains(services, d => d.ServiceType == typeof(LoggingTransactionObserver));
    }

    [Fact]
    public void AddTransactionalLogging_CalledTwice_RegistersOnlyOnce()
    {
        var services = NewServices()
            .AddTransactionalLogging()
            .AddTransactionalLogging();

        var count = services.Count(d => d.ServiceType == typeof(ITransactionObserver));

        Assert.Equal(1, count);
    }

    [Fact]
    public void AddTransactionalObserver_TwoDistinctTypes_RegistersBoth()
    {
        var services = NewServices()
            .AddTransactionalLogging()
            .AddTransactionalObserver<RecordingObserver>();

        var count = services.Count(d => d.ServiceType == typeof(ITransactionObserver));

        Assert.Equal(2, count);
    }

    // -------------------------------------------------------------------------
    // Attribute-on-interface discovery
    // -------------------------------------------------------------------------

    [Fact]
    public void AddTransactionalServices_AttributeOnInterfaceOnly_StillRegistersProxy()
    {
        var provider = NewServices()
            .AddTransactionalServices(Assembly.GetExecutingAssembly())
            .BuildServiceProvider();

        var svc = provider.GetRequiredService<IInterfaceOnlyAttrService>();

        Assert.IsAssignableFrom<IInterfaceOnlyAttrService>(svc);
        Assert.IsNotType<InterfaceOnlyAttrService>(svc); // must be a proxy
    }

    [Fact]
    public void AddTransactionalServices_AttributeOnInterfaceOnly_ProxyTransactsMethod()
    {
        var observer = new RecordingObserver();
        var proxy = Gsag.Transactional.Core.Proxy.TransactionProxyFactory.Create<IInterfaceOnlyAttrService>(
            new InterfaceOnlyAttrService(), observer);

        proxy.Run();

        Assert.Contains("COMMIT:Run", observer.Calls);
    }

    // -------------------------------------------------------------------------
    // AddTransactionalService<TInterface, TImplementation>
    // -------------------------------------------------------------------------

    [Fact]
    public void AddTransactionalService_RegistersInterfaceAsProxy()
    {
        var provider = NewServices()
            .AddTransactionalService<IManualService, ManualServiceImpl>()
            .BuildServiceProvider();

        var svc = provider.GetRequiredService<IManualService>();

        Assert.IsAssignableFrom<IManualService>(svc);
        Assert.IsNotType<ManualServiceImpl>(svc);
    }

    [Fact]
    public void AddTransactionalService_ProxiedService_IsScoped()
    {
        var services = NewServices()
            .AddTransactionalService<IManualService, ManualServiceImpl>();

        var descriptor = services.Single(d => d.ServiceType == typeof(IManualService));

        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    [Fact]
    public void AddTransactionalService_RegistersITransactionHooksAsSingleton()
    {
        var services = NewServices()
            .AddTransactionalService<IManualService, ManualServiceImpl>();

        var descriptor = services.Single(d => d.ServiceType == typeof(ITransactionHooks));

        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    // -------------------------------------------------------------------------
    // Composite observer branch — two observers both receive events
    // -------------------------------------------------------------------------

    [Fact]
    public void AddTransactionalServices_WithTwoObservers_CompositeDispatchesToBoth()
    {
        var obs1 = new RecordingObserver();
        var obs2 = new RecordingObserver();

        var composite = new Gsag.Transactional.Core.Observability.CompositeTransactionObserver([obs1, obs2]);
        var proxy = Gsag.Transactional.Core.Proxy.TransactionProxyFactory.Create<IExtTestService>(
            new ExtTestService(), composite);

        proxy.Do();

        Assert.Contains("COMMIT:Do", obs1.Calls);
        Assert.Contains("COMMIT:Do", obs2.Calls);
    }

    [Fact]
    public void AddTransactionalServices_WithTwoRegisteredObservers_BothReceiveBeginEvent()
    {
        var obs1 = new RecordingObserver();
        var obs2 = new RecordingObserver();
        var composite = new Gsag.Transactional.Core.Observability.CompositeTransactionObserver([obs1, obs2]);
        var proxy = Gsag.Transactional.Core.Proxy.TransactionProxyFactory.Create<IExtTestService>(
            new ExtTestService(), composite);

        proxy.Do();

        Assert.Contains("BEGIN:Do", obs1.Calls);
        Assert.Contains("BEGIN:Do", obs2.Calls);
    }

    // -------------------------------------------------------------------------
    // Namespace convention — interface in different namespace is NOT matched
    // -------------------------------------------------------------------------

    [Fact]
    public void AddTransactionalServices_ClassWhoseInterfaceIsInDifferentNamespace_IsNotRegistered()
    {
        // DifferentNamespaceService implements IDifferentNamespaceService, but that interface
        // lives in a different namespace — the convention check requires i.Namespace == serviceType.Namespace.
        var services = NewServices()
            .AddTransactionalServices(Assembly.GetExecutingAssembly());

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IDifferentNamespaceService));
    }

    /// <summary>
    /// Exercises the `_ => new CompositeTransactionObserver(registeredObservers)` branch in the
    /// DI factory lambda by registering two observers via the container and resolving the proxy
    /// through the container. Both observers must receive events — verifies the composite is built
    /// and not reduced to a single observer.
    /// </summary>
    [Fact]
    public void AddTransactionalServices_WithTwoObserversRegisteredViaDI_BothReceiveCommitEvent()
    {
        var obs1 = new RecordingObserver();
        var obs2 = new RecordingObserver();

        var provider = NewServices()
            .AddSingleton<ITransactionObserver>(obs1)
            .AddSingleton<ITransactionObserver>(obs2)
            .AddTransactionalServices(Assembly.GetExecutingAssembly())
            .BuildServiceProvider();

        using var scope = provider.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IExtTestService>();
        svc.Do();

        Assert.Contains("COMMIT:Do", obs1.Calls);
        Assert.Contains("COMMIT:Do", obs2.Calls);
    }

    /// <summary>
    /// Exercises the `1 => registeredObservers[0]` branch in the DI factory lambda —
    /// exactly one observer registered, must receive events without wrapping in Composite.
    /// </summary>
    [Fact]
    public void AddTransactionalServices_WithOneObserverRegisteredViaDI_ObserverReceivesCommitEvent()
    {
        var obs = new RecordingObserver();

        var provider = NewServices()
            .AddSingleton<ITransactionObserver>(obs)
            .AddTransactionalServices(Assembly.GetExecutingAssembly())
            .BuildServiceProvider();

        using var scope = provider.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IExtTestService>();
        svc.Do();

        Assert.Contains("COMMIT:Do", obs.Calls);
    }
}
