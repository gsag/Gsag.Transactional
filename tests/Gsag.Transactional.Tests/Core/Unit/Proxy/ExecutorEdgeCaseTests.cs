using System.Transactions;
using Gsag.Transactional.Core.Attributes;
using Gsag.Transactional.Core.Hooks;
using Gsag.Transactional.Core.Observability;
using Gsag.Transactional.Core.Proxy;
using Gsag.Transactional.Tests.Core.Unit;
using Xunit;

namespace Gsag.Transactional.Tests.Core.Unit.Proxy;

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
public class ThrowingOnBeginObserver : ITransactionObserver
{
    public void OnBegin(TransactionInfo info) =>
        throw new InvalidOperationException("begin-fail");
    public void OnCommit(TransactionInfo info, TimeSpan elapsed) { }
    public void OnRollback(TransactionInfo info, Exception exception, TimeSpan elapsed) { }
    public void OnComplete(TransactionInfo info, bool committed, TimeSpan elapsed) { }
}

// Observer that throws in OnRollback — exercises the double-fault path where
// scope.Dispose() throws TransactionAbortedException AND the observer also throws.
public class ThrowingOnRollbackObserver : ITransactionObserver
{
    public void OnBegin(TransactionInfo info) { }
    public void OnCommit(TransactionInfo info, TimeSpan elapsed) { }
    public void OnRollback(TransactionInfo info, Exception exception, TimeSpan elapsed) =>
        throw new InvalidOperationException("observer-rollback-fail");
    public void OnComplete(TransactionInfo info, bool committed, TimeSpan elapsed) { }
}

// Service that votes to abort the ambient transaction then returns normally.
// scope.Complete() succeeds (it only sets _complete=true and never throws with LTM).
// scope.Dispose() then throws TransactionAbortedException because the committable
// transaction was rolled back — exercising the disposeEx != null branch in SyncHandler.
public interface IForcedAbortService
{
    [Transactional]
    void ForceAbort();

    [Transactional]
    int ForceAbortWithReturn();
}

public class ForcedAbortService : IForcedAbortService
{
    public void ForceAbort() => Transaction.Current!.Rollback();
    public int ForceAbortWithReturn() { Transaction.Current!.Rollback(); return 42; }
}

// Task<T>-returning method that throws synchronously before returning its task.
// Exercises the returnType.IsGenericType branch in HandleAsync that creates a
// properly-typed faulted Task<T> via CreateFaultedTask rather than Task.FromException.
public interface IGenericTaskSyncThrowService
{
    [Transactional]
    Task<string> ThrowSynchronouslyBeforeTaskAsync();
}

public class GenericTaskSyncThrowService : IGenericTaskSyncThrowService
{
    public Task<string> ThrowSynchronouslyBeforeTaskAsync()
    {
        throw new InvalidOperationException("generic-task-sync-throw");
#pragma warning disable CS0162
        return Task.FromResult("unreachable");
#pragma warning restore CS0162
    }
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
    public void SyncReturn_WhenTransactionAbortedAfterComplete_PropagatesAbortedExceptionAndIgnoresResult()
    {
        // Explicit test for the non-void sync path: verifies that when scope.Dispose() throws,
        // the computed return value is discarded and TransactionAbortedException propagates.
        // Covers the disposeEx is not null branch in SyncHandler.Execute() for return-type methods.
        var observer = new RecordingObserver();
        var proxy = TransactionProxyFactory.Create<IForcedAbortService>(
            new ForcedAbortService(), observer);

        Assert.Throws<TransactionAbortedException>(() => proxy.ForceAbortWithReturn());

        Assert.Contains("ROLLBACK:ForceAbortWithReturn", observer.Calls);
        Assert.DoesNotContain("COMMIT:ForceAbortWithReturn", observer.Calls);
        Assert.Contains("COMPLETE:ForceAbortWithReturn:False", observer.Calls);
    }

    [Fact]
    public void BeginScope_WithUnsupportedPropagation_ThrowsArgumentOutOfRangeException()
    {
        var attr = new TransactionalAttribute { Propagation = (TransactionScopeOption)999 };

        Assert.Throws<ArgumentOutOfRangeException>(() => TransactionHooks.BeginScope(attr));
    }

    // The three tests below are async analogues of Dispose_WhenTransactionAbortedAfterComplete.
    // Each covers the disposeEx is not null branches in a different async wrapper:
    // ExecuteAsync<T>(Task<T>), ExecuteAsync(ValueTask), ExecuteAsync<T>(ValueTask<T>).
    // ExecuteAsync(Task) is already covered by HookErrorTests.

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

    [Fact]
    public void ForceAbort_OnComplete_CommittedIsFalse()
    {
        var observer = new RecordingObserver();
        var proxy = TransactionProxyFactory.Create<IForcedAbortService>(
            new ForcedAbortService(), observer);

        Assert.Throws<TransactionAbortedException>(() => proxy.ForceAbort());

        Assert.Contains("COMPLETE:ForceAbort:False", observer.Calls);
    }

    [Fact]
    public async Task HandleAsync_GenericTask_WhenThrowsSynchronouslyBeforeTask_OriginalExceptionPropagates()
    {
        var proxy = TransactionProxyFactory.Create<IGenericTaskSyncThrowService>(
            new GenericTaskSyncThrowService());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => proxy.ThrowSynchronouslyBeforeTaskAsync());

        Assert.Equal("generic-task-sync-throw", ex.Message);
    }

    [Fact]
    public async Task HandleAsync_GenericTask_WhenThrowsSynchronouslyBeforeTask_ObserverReceivesRollbackAndComplete()
    {
        var observer = new RecordingObserver();
        var proxy = TransactionProxyFactory.Create<IGenericTaskSyncThrowService>(
            new GenericTaskSyncThrowService(), observer);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => proxy.ThrowSynchronouslyBeforeTaskAsync());

        Assert.Contains("ROLLBACK:ThrowSynchronouslyBeforeTaskAsync", observer.Calls);
        Assert.Contains("COMPLETE:ThrowSynchronouslyBeforeTaskAsync:False", observer.Calls);
    }
}
