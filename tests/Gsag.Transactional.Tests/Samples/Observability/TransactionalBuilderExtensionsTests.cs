using Gsag.Transactional.Core.Extensions;
using Gsag.Transactional.Core.Observability;
using Gsag.Transactional.Observability.Extensions;
using Gsag.Transactional.Observability.Observers;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gsag.Transactional.Tests.Samples.Observability;

public class TransactionalBuilderExtensionsTests
{
    [Fact]
    public void AddObservability_RegistersOpenTelemetryTransactionObserver()
    {
        var services = new ServiceCollection();

        services.AddTransactional(b => b.AddObservability());

        using var provider = services.BuildServiceProvider();
        var observer = provider.GetRequiredService<OpenTelemetryTransactionObserver>();
        var registeredObserver = Assert.Single(provider.GetServices<ITransactionObserver>());
        Assert.Same(observer, registeredObserver);
    }

    [Fact]
    public void AddObservability_WhenCalledTwice_RegistersSingleObserver()
    {
        var services = new ServiceCollection();

        services.AddTransactional(b => b
            .AddObservability()
            .AddObservability());

        using var provider = services.BuildServiceProvider();
        Assert.Single(provider.GetServices<ITransactionObserver>());
    }
}
