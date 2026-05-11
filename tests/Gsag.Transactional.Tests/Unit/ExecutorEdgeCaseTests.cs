using System.Reflection;
using System.Transactions;
using Transactional.Core.Attributes;
using Transactional.Core.Hooks;
using Transactional.Core.Observability;
using Transactional.Core.Proxy;
using Xunit;

namespace Transactional.Tests.Unit;

// Async services that vote to abort the ambient transaction then return normally.
// scope.Complete() does not throw on the aborted transaction — scope.Dispose() does.
// Each return type exercises a different async wrapper's disposeEx branch.
public interface IForcedAbortAsyncService
{
    [Transactional]
    Task<int> ForceAbortGenericTaskAsync();

    [Transactional]
    ValueTask ForceAbortValueTaskAsync();

    [Transactional]
    ValueTask<int> ForceAbortGenericValueTaskAsync();
}

public class ForcedAbortAsyncService : IForcedAbortAsyncService
{
    public async Task<int> ForceAbortGenericTaskAsync()
    {
        await Task.CompletedTask;
        Transaction.Current!.Rollback();
        return 0;
    }

    public async ValueTask ForceAbortValueTaskAsync()
    {
        await Task.CompletedTask;
        Transaction.Current!.Rollback();
    }

    public async ValueTask<int> ForceAbortGenericValueTaskAsync()
    {
        await Task.CompletedTask;
        Transaction.Current!.Rollback();
        return 0;
    }
}

// Observer that throws in OnBegin — exercises the OpenScope catch block that
// disposes the already-created TransactionScope and clears the hook AsyncLocal.
public class ThrowingOnBeginObserver : ITransactionLifecycleObserver
{
    public void OnBegin(MethodInfo method, TransactionalAttribute attr) =>
        throw new InvalidOperationException("begin-fail");
    public void OnCommit(MethodInfo method, TimeSpan elapsed) { }
    public void OnRollback(MethodInfo method, Exception exception, TimeSpan elapsed) { }
    public void OnComplete(MethodInfo method, bool committed, TimeSpan elapsed) { }
}

// Observer that throws in OnRollback — exercises the double-fault path where
// scope.Dispose() throws TransactionAbortedException AND the observer also throws.
public class ThrowingOnRollbackObserver : ITransactionLifecycleObserver
{
    public void OnBegin(MethodInfo method, TransactionalAttribute attr) { }
    public void OnCommit(MethodInfo method, TimeSpan elapsed) { }
    public void OnRollback(MethodInfo method, Exception exception, TimeSpan elapsed) =>
        throw new InvalidOperationException("observer-rollback-fail");
    public void OnComplete(MethodInfo method, bool committed, TimeSpan elapsed) { }
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
        Assert.Contains("ROLLBACK:ForceAbort", observer.Calls);
        Assert.DoesNotContain("COMMIT:ForceAbort", observer.Calls);
    }

    [Fact]
    public void BeginScope_WithUnsupportedPropagation_ThrowsArgumentOutOfRangeException()
    {
        var attr = new TransactionalAttribute { Propagation = (TransactionScopeOption)999 };

        Assert.Throws<ArgumentOutOfRangeException>(() => TransactionHooks.BeginScope(attr));
    }

    // The three tests below are async analogues of Dispose_WhenTransactionAbortedAfterComplete.
    // Each covers the disposeEx is not null branches in a different async wrapper:
    // WrapGenericTaskAsync, WrapVoidValueTaskAsync, WrapGenericValueTaskAsync.
    // WrapVoidTaskAsync is already covered by HookErrorTests.

    [Fact]
    public async Task WrapGenericTask_WhenTransactionAbortedAfterComplete_PropagatesAbortedException()
    {
        var proxy = TransactionProxyFactory.Create<IForcedAbortAsyncService>(
            new ForcedAbortAsyncService());

        await Assert.ThrowsAsync<TransactionAbortedException>(
            () => proxy.ForceAbortGenericTaskAsync());
    }

    [Fact]
    public async Task WrapVoidValueTask_WhenTransactionAbortedAfterComplete_PropagatesAbortedException()
    {
        var proxy = TransactionProxyFactory.Create<IForcedAbortAsyncService>(
            new ForcedAbortAsyncService());

        await Assert.ThrowsAsync<TransactionAbortedException>(
            () => proxy.ForceAbortValueTaskAsync().AsTask());
    }

    [Fact]
    public async Task WrapGenericValueTask_WhenTransactionAbortedAfterComplete_PropagatesAbortedException()
    {
        var proxy = TransactionProxyFactory.Create<IForcedAbortAsyncService>(
            new ForcedAbortAsyncService());

        await Assert.ThrowsAsync<TransactionAbortedException>(
            () => proxy.ForceAbortGenericValueTaskAsync().AsTask());
    }

    /// <summary>
    /// Double-fault: scope.Dispose() throws TransactionAbortedException AND the observer
    /// also throws in OnRollback. NotifyCommitOutcome calls ThrowingOnRollbackObserver.OnRollback,
    /// which throws before ExceptionDispatchInfo.Capture(disposeEx).Throw() is reached —
    /// so the observer exception escapes the finally block and masks the disposeEx.
    /// </summary>
    [Fact]
    public void Dispose_WhenAbortedAndObserverOnRollbackThrows_ObserverExceptionPropagates()
    {
        var proxy = TransactionProxyFactory.Create<IForcedAbortService>(
            new ForcedAbortService(), new ThrowingOnRollbackObserver());

        var ex = Assert.Throws<InvalidOperationException>(() => proxy.ForceAbort());

        Assert.Equal("observer-rollback-fail", ex.Message);
    }
}
