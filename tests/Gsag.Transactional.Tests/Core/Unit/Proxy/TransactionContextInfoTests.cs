using System.Reflection.Emit;
using System.Transactions;
using Gsag.Transactional.Core.Attributes;
using Gsag.Transactional.Core.Hooks;
using Gsag.Transactional.Core.Observability;
using Gsag.Transactional.Core.Proxy;
using Xunit;

namespace Gsag.Transactional.Tests.Core.Unit.Proxy;

public interface IContextInfoService
{
    [Transactional(
        IsolationLevel = IsolationLevel.Serializable,
        Propagation = TransactionScopeOption.RequiresNew,
        TimeoutSeconds = 5)]
    void WithConfig();

    [Transactional]
    void WithDefaults();
}

public class ContextInfoService : IContextInfoService
{
    public void WithConfig() { }
    public void WithDefaults() { }
}

internal sealed class CapturingObserver : ITransactionObserver
{
    public TransactionInfo? Captured { get; private set; }

    public void OnBegin(TransactionInfo info) => Captured = info;
    public void OnCommit(TransactionInfo info, TimeSpan elapsed) { }
    public void OnRollback(TransactionInfo info, Exception exception, TimeSpan elapsed) { }
    public void OnComplete(TransactionInfo info, bool committed, TimeSpan elapsed) { }
}

public class TransactionContextInfoTests
{
    private static (IContextInfoService proxy, CapturingObserver observer) Build()
    {
        var observer = new CapturingObserver();
        var proxy = TransactionProxyFactory.Create<IContextInfoService>(new ContextInfoService(), observer);
        return (proxy, observer);
    }

    [Fact]
    public void Info_MethodName_MatchesInvokedMethod()
    {
        var (proxy, observer) = Build();

        proxy.WithConfig();

        Assert.Equal("WithConfig", observer.Captured!.MethodName);
    }

    [Fact]
    public void Info_DeclaringType_MatchesInterfaceType()
    {
        var (proxy, observer) = Build();

        proxy.WithConfig();

        Assert.Equal(typeof(IContextInfoService), observer.Captured!.DeclaringType);
    }

    [Fact]
    public void Info_DeclaringType_FallsBackToObject_WhenMethodDeclaringTypeIsNull()
    {
        var dm = new DynamicMethod("Orphan", typeof(void), Type.EmptyTypes);
        using var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);
        var ctx = new TransactionContext(dm, scope, new TransactionalAttribute(), new CapturingObserver(), new HookCollection());

        Assert.Equal(typeof(object), ctx.Info.DeclaringType);
    }

    [Fact]
    public void Info_IsolationLevel_MatchesAttribute()
    {
        var (proxy, observer) = Build();

        proxy.WithConfig();

        Assert.Equal(IsolationLevel.Serializable, observer.Captured!.IsolationLevel);
    }

    [Fact]
    public void Info_Propagation_MatchesAttribute()
    {
        var (proxy, observer) = Build();

        proxy.WithConfig();

        Assert.Equal(TransactionScopeOption.RequiresNew, observer.Captured!.Propagation);
    }

    [Fact]
    public void Info_TimeoutSeconds_IsSet_WhenAttributeHasPositiveTimeout()
    {
        var (proxy, observer) = Build();

        proxy.WithConfig();

        Assert.Equal(5, observer.Captured!.TimeoutSeconds);
    }

    [Fact]
    public void Info_TimeoutSeconds_IsNull_WhenAttributeHasZeroTimeout()
    {
        var (proxy, observer) = Build();

        proxy.WithDefaults();

        Assert.Null(observer.Captured!.TimeoutSeconds);
    }

    [Fact]
    public void Info_DefaultIsolationLevel_IsReadCommitted()
    {
        var (proxy, observer) = Build();

        proxy.WithDefaults();

        Assert.Equal(IsolationLevel.ReadCommitted, observer.Captured!.IsolationLevel);
    }

    [Fact]
    public void Info_DefaultPropagation_IsRequired()
    {
        var (proxy, observer) = Build();

        proxy.WithDefaults();

        Assert.Equal(TransactionScopeOption.Required, observer.Captured!.Propagation);
    }

    [Fact]
    public void Info_DefaultMethodName_MatchesWithDefaultsMethod()
    {
        var (proxy, observer) = Build();

        proxy.WithDefaults();

        Assert.Equal("WithDefaults", observer.Captured!.MethodName);
    }
}