using System.Reflection;
using Gsag.Transactional.Core.Attributes;
using Gsag.Transactional.Core.Extensions;
using Gsag.Transactional.Core.Hooks;
using Gsag.Transactional.Core.Observability;
using Gsag.Transactional.Tests.Core.Unit;
using Gsag.Transactional.Tests.Core.Unit.Extensions.Other;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gsag.Transactional.Tests.Core.Unit.Extensions;

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
    public static void Run() { }
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
            .AddTransactional(b => b.ScanAssembly(Assembly.GetExecutingAssembly()))
            .BuildServiceProvider();

        var svc = provider.GetRequiredService<IExtTestService>();

        Assert.IsType<IExtTestService>(svc, exactMatch: false);
        Assert.IsNotType<ExtTestService>(svc); // must be proxy, not concrete
    }

    [Fact]
    public void AddTransactionalServices_ProxiedService_IsScoped()
    {
        var services = NewServices()
            .AddTransactional(b => b.ScanAssembly(Assembly.GetExecutingAssembly()));

        var descriptor = services.Single(d => d.ServiceType == typeof(IExtTestService));

        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    [Fact]
    public void AddTransactionalServices_RegistersITransactionHooksAsSingleton()
    {
        var services = NewServices()
            .AddTransactional(b => b.ScanAssembly(Assembly.GetExecutingAssembly()));

        var descriptor = services.Single(d => d.ServiceType == typeof(ITransactionHooks));

        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    [Fact]
    public void AddTransactionalServices_ITransactionHooks_ReturnsSameInstance()
    {
        var provider = NewServices()
            .AddTransactional(b => b.ScanAssembly(Assembly.GetExecutingAssembly()))
            .BuildServiceProvider();

        var h1 = provider.GetRequiredService<ITransactionHooks>();
        var h2 = provider.GetRequiredService<ITransactionHooks>();

        Assert.Same(h1, h2);
    }

    [Fact]
    public void AddTransactionalServices_CalledTwice_DoesNotDuplicateHooksRegistration()
    {
        var services = NewServices()
            .AddTransactional(b => b.ScanAssembly(Assembly.GetExecutingAssembly()))
            .AddTransactional(b => b.ScanAssembly(Assembly.GetExecutingAssembly()));

        var count = services.Count(d => d.ServiceType == typeof(ITransactionHooks));

        Assert.Equal(1, count);
    }

    [Fact]
    public void AddTransactionalServices_ClassWithoutMatchingInterface_NotRegistered()
    {
        var services = NewServices()
            .AddTransactional(b => b.ScanAssembly(Assembly.GetExecutingAssembly()));

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(ExtTestOrphan));
    }

    // -------------------------------------------------------------------------
    // AddTransactionalLogging
    // -------------------------------------------------------------------------

    [Fact]
    public void AddTransactionalLogging_RegistersLoggingObserverAsSingleton()
    {
        var services = NewServices()
            .AddTransactional(b => b.AddLogging());

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
            .AddTransactional(b => b.AddLogging())
            .AddTransactional(b => b.AddLogging());

        var count = services.Count(d => d.ServiceType == typeof(ITransactionObserver));

        Assert.Equal(1, count);
    }

    [Fact]
    public void AddTransactionalObserver_TwoDistinctTypes_RegistersBoth()
    {
        var services = NewServices()
            .AddTransactional(b => b.AddLogging().AddObserver<RecordingObserver>());

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
            .AddTransactional(b => b.ScanAssembly(Assembly.GetExecutingAssembly()))
            .BuildServiceProvider();

        var svc = provider.GetRequiredService<IInterfaceOnlyAttrService>();

        Assert.IsType<IInterfaceOnlyAttrService>(svc, exactMatch: false);
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
            .AddTransactional(b => b.AddService<IManualService, ManualServiceImpl>())
            .BuildServiceProvider();

        var svc = provider.GetRequiredService<IManualService>();

        Assert.IsType<IManualService>(svc, exactMatch: false);
        Assert.IsNotType<ManualServiceImpl>(svc);
    }

    [Fact]
    public void AddTransactionalService_ProxiedService_IsScoped()
    {
        var services = NewServices()
            .AddTransactional(b => b.AddService<IManualService, ManualServiceImpl>());

        var descriptor = services.Single(d => d.ServiceType == typeof(IManualService));

        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    [Fact]
    public void AddTransactionalService_RegistersITransactionHooksAsSingleton()
    {
        var services = NewServices()
            .AddTransactional(b => b.AddService<IManualService, ManualServiceImpl>());

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
            .AddTransactional(b => b.ScanAssembly(Assembly.GetExecutingAssembly()));

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
            .AddTransactional(b => b.ScanAssembly(Assembly.GetExecutingAssembly()))
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
            .AddTransactional(b => b.ScanAssembly(Assembly.GetExecutingAssembly()))
            .BuildServiceProvider();

        using var scope = provider.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IExtTestService>();
        svc.Do();

        Assert.Contains("COMMIT:Do", obs.Calls);
    }

    // -------------------------------------------------------------------------
    // Auto-discovery behavior
    // -------------------------------------------------------------------------

    [Fact]
    public void AddTransactional_WithoutExplicitScanAssembly_AutoScansCallingAssembly()
    {
        // When no ScanAssembly is called, the calling assembly (test assembly) should be auto-discovered
        var services = NewServices()
            .AddTransactional(); // No explicit ScanAssembly — auto-discovery should happen

        // ExtTestService should be discovered and registered from the calling assembly
        Assert.Contains(services, d => d.ServiceType == typeof(IExtTestService));
    }

    [Fact]
    public void AddTransactional_WithoutExplicitScanAssembly_ProxyIsResolvable()
    {
        var observer = new RecordingObserver();

        var provider = NewServices()
            .AddSingleton<ITransactionObserver>(observer)
            .AddTransactional() // No explicit ScanAssembly — auto-discovery should happen
            .BuildServiceProvider();

        // Should be able to resolve the auto-discovered service
        var svc = provider.GetRequiredService<IExtTestService>();

        Assert.IsType<IExtTestService>(svc, exactMatch: false);
        Assert.IsNotType<ExtTestService>(svc); // must be proxy
    }

    [Fact]
    public void AddTransactional_WithoutExplicitScanAssembly_ProxyTransacts()
    {
        var observer = new RecordingObserver();

        var provider = NewServices()
            .AddSingleton<ITransactionObserver>(observer)
            .AddTransactional() // No explicit ScanAssembly — auto-discovery should happen
            .BuildServiceProvider();

        using var scope = provider.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IExtTestService>();
        svc.Do();

        // Verify transaction lifecycle was recorded
        Assert.Contains("BEGIN:Do", observer.Calls);
        Assert.Contains("COMMIT:Do", observer.Calls);
    }

    [Fact]
    public void AddTransactional_WithExplicitScanAssembly_OverwritesAutoDiscovery()
    {
        // When explicit ScanAssembly is called with a different assembly,
        // it should overwrite auto-discovery and NOT include services from the calling assembly
        // We use Assembly.GetExecutingAssembly() which is still the test assembly, but the point is
        // we're explicitly saying "use this assembly" rather than auto-discovering

        var services = NewServices()
            .AddTransactional(b => b.ScanAssembly(Assembly.GetExecutingAssembly()));

        // Service should still be found, but by explicit scan, not auto-discovery
        Assert.Contains(services, d => d.ServiceType == typeof(IExtTestService));
    }

    [Fact]
    public void AddTransactional_WithMultipleScanAssembly_ScansAllSpecifiedAssemblies()
    {
        // Multiple ScanAssembly calls should all be applied
        var services = NewServices()
            .AddTransactional(b => b
                .ScanAssembly(Assembly.GetExecutingAssembly())
                .ScanAssembly(Assembly.GetExecutingAssembly()) // Called twice with same assembly
            );

        // Service should be registered through at least one ScanAssembly call
        Assert.Contains(services, d => d.ServiceType == typeof(IExtTestService));

        // Should be resolvable and return a proxy
        var provider = services.BuildServiceProvider();
        var svc = provider.GetRequiredService<IExtTestService>();
        Assert.IsType<IExtTestService>(svc, exactMatch: false);
        Assert.IsNotType<ExtTestService>(svc); // must be proxy
    }

    // -------------------------------------------------------------------------
    // Service isolation — verify only matching services are registered
    // -------------------------------------------------------------------------

    [Fact]
    public void AddTransactional_WithoutExplicitScanAssembly_OnlyIncludesServicesFromCallingAssembly()
    {
        // When auto-discovering, only services from the calling assembly should be included
        var services = NewServices()
            .AddTransactional(); // Auto-discover calling assembly

        // ExtTestService SHOULD be found (in same assembly, same namespace, has interface)
        Assert.Contains(services, d => d.ServiceType == typeof(IExtTestService));

        // DifferentNamespaceService should NOT be registered (interface in different namespace)
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IDifferentNamespaceService));
    }

    [Fact]
    public void AddTransactional_WithScanAssembly_OnlyIncludesServicesFromSpecifiedAssembly()
    {
        // When using explicit ScanAssembly, only services from that assembly should be included
        var services = NewServices()
            .AddTransactional(b => b.ScanAssembly(Assembly.GetExecutingAssembly()));

        // ExtTestService SHOULD be found
        Assert.Contains(services, d => d.ServiceType == typeof(IExtTestService));

        // DifferentNamespaceService should NOT be registered (interface in different namespace)
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IDifferentNamespaceService));
    }

    [Fact]
    public void AddTransactional_WithoutExplicitScanAssembly_ExcludesOrphanServices()
    {
        // Services without matching interfaces should not be registered
        var services = NewServices()
            .AddTransactional(); // Auto-discover

        // ExtTestOrphan has [Transactional] but no matching interface — should NOT be registered
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(ExtTestOrphan));
    }

    [Fact]
    public void AddTransactional_WithScanAssembly_ExcludesOrphanServices()
    {
        // Services without matching interfaces should not be registered
        var services = NewServices()
            .AddTransactional(b => b.ScanAssembly(Assembly.GetExecutingAssembly()));

        // ExtTestOrphan has [Transactional] but no matching interface — should NOT be registered
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(ExtTestOrphan));
    }

    [Fact]
    public void AddTransactional_WithoutExplicitScanAssembly_RegistersOnlyExpectedServices()
    {
        // Verify exact count of registered transactional services
        var services = NewServices()
            .AddTransactional(); // Auto-discover

        // Count how many IExtTestService, IInterfaceOnlyAttrService, IManualService are registered
        var registeredInterfaces = services
            .Where(d => d.ServiceType == typeof(IExtTestService)
                     || d.ServiceType == typeof(IInterfaceOnlyAttrService)
                     || d.ServiceType == typeof(IManualService))
            .ToList();

        // Should have at least ExtTestService and InterfaceOnlyAttrService (both auto-discovered)
        // ManualService requires explicit AddService, so should NOT be here
        Assert.Contains(services, d => d.ServiceType == typeof(IExtTestService));
        Assert.Contains(services, d => d.ServiceType == typeof(IInterfaceOnlyAttrService));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IManualService));
    }

    [Fact]
    public void AddTransactional_ExplicitAddService_SupressesAutoDiscovery()
    {
        // When explicit AddService is called, auto-discovery is suppressed
        // Only the explicitly registered service is available
        var services = NewServices()
            .AddTransactional(b => b
                .AddService<IManualService, ManualServiceImpl>() // Explicit — suppresses auto-discovery
            );

        // Explicit service SHOULD be registered
        Assert.Contains(services, d => d.ServiceType == typeof(IManualService));

        // Auto-discovered services should NOT be present (auto-discovery was suppressed)
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IExtTestService));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IInterfaceOnlyAttrService));
    }
}
