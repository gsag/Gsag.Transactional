using System.Reflection;
using System.Transactions;
using Transactional.Core.Attributes;
using Transactional.Core.Hooks;
using Transactional.Core.Observability;
using Transactional.Core.Proxy;
using Xunit;

namespace Transactional.Tests.Unit;

// Observer that throws in OnBegin — exercises the OpenScope catch block that
// disposes the already-created TransactionScope and clears the hook AsyncLocal.
public class ThrowingOnBeginObserver : ITransactionLifecycleObserver
{
    public void OnBegin(MethodInfo method, TransactionalAttribute attr) =>
        throw new InvalidOperationException("begin-fail");
    public void OnCommit(MethodInfo method, TimeSpan elapsed) { }
    public void OnRollback(MethodInfo method, Exception exception, TimeSpan elapsed) { }
}

// Service that votes to abort the ambient transaction then returns normally.
// When the proxy subsequently calls scope.Complete(), TransactionAbortedException is
// thrown — this exercises the Commit() catch block that calls OnRollback before rethrowing.
public interface IForcedAbortService
{
    [Transactional]
    void ForceAbort();
}

public class ForcedAbortService : IForcedAbortService
{
    public void ForceAbort() => Transaction.Current!.Rollback();
}

public class ExecutorEdgeCaseTests
{
    [Fact]
    public void OpenScope_WhenObserverOnBeginThrows_PropagatesException()
    {
        var proxy = TransactionProxyFactory.Create<IBasicService>(
            new BasicService(), new ThrowingOnBeginObserver());

        var ex = Assert.Throws<InvalidOperationException>(() => proxy.SyncReturn());

        Assert.Equal("begin-fail", ex.Message);
    }

    [Fact]
    public void Dispose_WhenTransactionAbortedAfterComplete_PropagatesAbortedException()
    {
        // scope.Complete() succeeds (does not throw) even after Transaction.Rollback().
        // scope.Dispose() then throws TransactionAbortedException, which exercises the
        // TryDispose catch block and the disposeEx != null branch in HandleSync.
        var observer = new RecordingObserver();
        var proxy = TransactionProxyFactory.Create<IForcedAbortService>(
            new ForcedAbortService(), observer);

        Assert.Throws<TransactionAbortedException>(() => proxy.ForceAbort());

        Assert.Contains("BEGIN:ForceAbort", observer.Calls);
        Assert.Contains("COMMIT:ForceAbort", observer.Calls);
    }

    [Fact]
    public void BeginScope_WithUnsupportedPropagation_ThrowsArgumentOutOfRangeException()
    {
        var attr = new TransactionalAttribute { Propagation = (TransactionScopeOption)999 };

        Assert.Throws<ArgumentOutOfRangeException>(() => TransactionHooks.BeginScope(attr));
    }
}
