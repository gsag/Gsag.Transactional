using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Transactional.Core.Attributes;
using Transactional.Core.Extensions;
using Transactional.Core.Hooks;
using Transactional.Core.Observability;
using Xunit;

namespace Transactional.Tests.Unit;

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

        // ITransactionLifecycleObserver is registered via factory (forwarding to LoggingTransactionObserver).
        var descriptor = services.Single(d => d.ServiceType == typeof(ITransactionLifecycleObserver));
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

        var count = services.Count(d => d.ServiceType == typeof(ITransactionLifecycleObserver));

        Assert.Equal(1, count);
    }

    [Fact]
    public void AddTransactionalObserver_TwoDistinctTypes_RegistersBoth()
    {
        var services = NewServices()
            .AddTransactionalLogging()
            .AddTransactionalObserver<RecordingObserver>();

        var count = services.Count(d => d.ServiceType == typeof(ITransactionLifecycleObserver));

        Assert.Equal(2, count);
    }
}
