using System.Transactions;
using Gsag.Transactional.Core.Attributes;
using Gsag.Transactional.Core.Hooks;
using Gsag.Transactional.Core.Observability;
using Gsag.Transactional.Core.Proxy;
using Xunit;

namespace Gsag.Transactional.Tests.Integration.Hooks;

// ---------------------------------------------------------------------------
// NoRollbackFor + hook masking
// ---------------------------------------------------------------------------

public interface INoRollbackHookService
{
    Task RunAsync();
}

public class NoRollbackHookService : INoRollbackHookService
{
    private readonly ITransactionHooks _hooks;
    public NoRollbackHookService(ITransactionHooks hooks) => _hooks = hooks;

    [Transactional(NoRollbackFor = [typeof(InvalidOperationException)])]
    public async Task RunAsync()
    {
        _hooks.AfterCommit(async () =>
        {
            await Task.CompletedTask;
            throw new Exception("hook failure");
        });
        await Task.CompletedTask;
        throw new InvalidOperationException("business error");
    }
}

// ---------------------------------------------------------------------------
// Scope.Complete() throwing (Transaction.Rollback() voted before Complete)
// ---------------------------------------------------------------------------

public interface IAbortBeforeCompleteService
{
    Task RunAsync();
}

public class AbortBeforeCompleteService : IAbortBeforeCompleteService
{
    [Transactional]
    public async Task RunAsync()
    {
        await Task.CompletedTask;
        Transaction.Current!.Rollback(); // vote to abort — Complete() will throw TransactionAbortedException
    }
}

// Minimal async service used to verify AsyncLocal is clean after an error
public interface ISimpleAsyncHookService
{
    Task RunAndCommitAsync();
}

public class SimpleAsyncHookService : ISimpleAsyncHookService
{
    private readonly ITransactionHooks _hooks;
    public List<string> Fired { get; } = [];
    public SimpleAsyncHookService(ITransactionHooks hooks) => _hooks = hooks;

    [Transactional]
    public async Task RunAndCommitAsync()
    {
        await Task.CompletedTask;
        _hooks.AfterCommit(async () =>
        {
            await Task.CompletedTask;
            Fired.Add("async-hook");
        });
    }
}

// ---------------------------------------------------------------------------
// ValueTask<T> + NoRollbackFor
// ---------------------------------------------------------------------------

public interface IValueTaskNoRollbackService
{
    ValueTask<string> RunAsync();
}

public class ValueTaskNoRollbackService : IValueTaskNoRollbackService
{
    private readonly ITransactionHooks _hooks;
    public List<string> Fired { get; } = [];
    public ValueTaskNoRollbackService(ITransactionHooks hooks) => _hooks = hooks;

    [Transactional(NoRollbackFor = [typeof(InvalidOperationException)])]
    public async ValueTask<string> RunAsync()
    {
        _hooks.AfterCommit(() => Fired.Add("hook"));
        await Task.CompletedTask;
        throw new InvalidOperationException("expected");
    }
}

// ---------------------------------------------------------------------------
// NoRollbackFor path — AfterRollback hook must not fire when tx committed
// ---------------------------------------------------------------------------

public interface INoRollbackWithAfterRollbackHookService
{
    Task RunAsync();
}

public class NoRollbackWithAfterRollbackHookService : INoRollbackWithAfterRollbackHookService
{
    private readonly ITransactionHooks _hooks;
    public List<string> Fired { get; } = [];
    public NoRollbackWithAfterRollbackHookService(ITransactionHooks hooks) => _hooks = hooks;

    [Transactional(NoRollbackFor = [typeof(InvalidOperationException)])]
    public async Task RunAsync()
    {
        _hooks.AfterRollback(() => Fired.Add("after-rollback"));
        _hooks.AfterCompletion(() => Fired.Add("after-completion"));
        await Task.CompletedTask;
        throw new InvalidOperationException("no-rollback");
    }
}

// ---------------------------------------------------------------------------
// Transaction.Current.Rollback() + hook registration
// ---------------------------------------------------------------------------

public interface IAbortWithHookService
{
    Task RunAsync();
}

public class AbortWithHookService : IAbortWithHookService
{
    private readonly ITransactionHooks _hooks;
    public List<string> Fired { get; } = [];
    public AbortWithHookService(ITransactionHooks hooks) => _hooks = hooks;

    [Transactional]
    public async Task RunAsync()
    {
        _hooks.AfterRollback(() => Fired.Add("after-rollback"));
        _hooks.AfterCompletion(() => Fired.Add("after-completion"));
        await Task.CompletedTask;
        Transaction.Current!.Rollback();
    }
}

// ---------------------------------------------------------------------------
// Observer OnBegin throws
// ---------------------------------------------------------------------------

public class ThrowOnBeginObserver : ITransactionObserver
{
    public void OnBegin(TransactionInfo info) =>
        throw new InvalidOperationException("observer refused");
    public void OnCommit(TransactionInfo info, TimeSpan elapsed) { }
    public void OnRollback(TransactionInfo info, Exception exception, TimeSpan elapsed) { }
    public void OnComplete(TransactionInfo info, bool committed, TimeSpan elapsed) { }
}

// ---------------------------------------------------------------------------
// Observer OnRollback throws
// ---------------------------------------------------------------------------

public class ThrowOnRollbackObserver : ITransactionObserver
{
    public void OnBegin(TransactionInfo info) { }
    public void OnCommit(TransactionInfo info, TimeSpan elapsed) { }
    public void OnRollback(TransactionInfo info, Exception exception, TimeSpan elapsed) =>
        throw new InvalidOperationException("observer rollback refused");
    public void OnComplete(TransactionInfo info, bool committed, TimeSpan elapsed) { }
}

public interface IThrowSyncService
{
    Task RunAsync();
}

public class ThrowSyncService : IThrowSyncService
{
    [Transactional]
    public Task RunAsync()
    {
        throw new InvalidOperationException("sync throw");
    }
}

// Throws after yielding — exercises the async wrapper catch path (not the synchronous preamble).
public interface IThrowAsyncBodyService
{
    Task RunAsync();
}

public class ThrowAsyncBodyService : IThrowAsyncBodyService
{
    [Transactional]
    public async Task RunAsync()
    {
        await Task.CompletedTask;
        throw new InvalidOperationException("async throw");
    }
}

// ---------------------------------------------------------------------------

public class HookErrorTests
{
    /// <summary>
    /// When a NoRollbackFor exception is in-flight and a hook also throws,
    /// the hook's AggregateException must not replace the original business exception.
    /// </summary>
    [Fact]
    public async Task NoRollbackFor_WhenHookThrows_BusinessExceptionIsNotMasked()
    {
        var hooks = new TransactionHooks();
        var svc = new NoRollbackHookService(hooks);
        var proxy = TransactionProxyFactory.Create<INoRollbackHookService>(svc, observer: null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => proxy.RunAsync());

        Assert.Equal("business error", ex.Message);
    }

    /// <summary>
    /// When Rollback() is called inside a [Transactional] method, Scope.Dispose() throws
    /// TransactionAbortedException. Verifies the exception propagates and that ClearScope ran
    /// (AsyncLocal clean) so subsequent calls work normally.
    /// </summary>
    [Fact]
    public async Task TransactionAborted_WhenRollbackCalledInsideMethod_ExceptionPropagatesAndScopeIsClean()
    {
        var hooks = new TransactionHooks();
        var svc = new AbortBeforeCompleteService();
        var proxy = TransactionProxyFactory.Create<IAbortBeforeCompleteService>(svc, observer: null);

        await Assert.ThrowsAsync<TransactionAbortedException>(() => proxy.RunAsync());

        // ClearScope must have run despite Dispose() throwing — subsequent call must succeed.
        var hookSvc = new SimpleAsyncHookService(hooks);
        var hookProxy = TransactionProxyFactory.Create<ISimpleAsyncHookService>(hookSvc, observer: null);
        await hookProxy.RunAndCommitAsync();
        Assert.Equal(["async-hook"], hookSvc.Fired);
    }

    /// <summary>
    /// ValueTask&lt;T&gt; methods with NoRollbackFor must commit and fire hooks
    /// even when the method throws the excluded exception type.
    /// </summary>
    [Fact]
    public async Task ValueTaskT_NoRollbackFor_CommitsAndRunsHooks()
    {
        var hooks = new TransactionHooks();
        var svc = new ValueTaskNoRollbackService(hooks);
        var proxy = TransactionProxyFactory.Create<IValueTaskNoRollbackService>(svc, observer: null);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await proxy.RunAsync());

        Assert.Equal(["hook"], svc.Fired);
    }

    /// <summary>
    /// Regression: when Scope.Dispose() throws TransactionAbortedException (because
    /// Transaction.Current.Rollback() was called inside the method), hooks registered before
    /// the abort must still fire — they must not be silently dropped by the Dispose exception.
    /// </summary>
    [Fact]
    public async Task TransactionAborted_WithAfterRollbackHook_HookStillFires()
    {
        var hooks = new TransactionHooks();
        var svc = new AbortWithHookService(hooks);
        var proxy = TransactionProxyFactory.Create<IAbortWithHookService>(svc, observer: null);

        await Assert.ThrowsAsync<TransactionAbortedException>(() => proxy.RunAsync());

        Assert.Contains("after-rollback", svc.Fired);
    }

    [Fact]
    public async Task TransactionAborted_WithAfterCompletionHook_HookStillFires()
    {
        var hooks = new TransactionHooks();
        var svc = new AbortWithHookService(hooks);
        var proxy = TransactionProxyFactory.Create<IAbortWithHookService>(svc, observer: null);

        await Assert.ThrowsAsync<TransactionAbortedException>(() => proxy.RunAsync());

        Assert.Contains("after-completion", svc.Fired);
    }

    /// <summary>
    /// If observer.OnBegin throws, OpenScope must restore the AsyncLocal and propagate the exception.
    /// A subsequent call through a normal proxy must succeed.
    /// </summary>
    [Fact]
    public async Task OpenScope_WhenObserverOnBeginThrows_AsyncLocalRestoredAndExceptionPropagates()
    {
        var hooks = new TransactionHooks();
        var svc = new SimpleAsyncHookService(hooks);
        var badProxy = TransactionProxyFactory.Create<ISimpleAsyncHookService>(svc, new ThrowOnBeginObserver());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => badProxy.RunAndCommitAsync());
        Assert.Equal("observer refused", ex.Message);

        // AsyncLocal must be clean — a subsequent call on a normal proxy must succeed.
        var goodProxy = TransactionProxyFactory.Create<ISimpleAsyncHookService>(svc, observer: null);
        await goodProxy.RunAndCommitAsync();
        Assert.Equal(["async-hook"], svc.Fired);
    }

    /// <summary>
    /// Regression: when observer.OnRollback throws inside the synchronous preamble catch block
    /// (the method threw before returning its Task), TryDispose must still run so the AsyncLocal
    /// hook slot is restored and subsequent calls operate on a clean scope.
    /// </summary>
    [Fact]
    public async Task HandleAsync_WhenObserverOnRollbackThrows_AsyncLocalRestoredAndExceptionPropagates()
    {
        var hooks = new TransactionHooks();
        var svc = new ThrowSyncService();
        var badProxy = TransactionProxyFactory.Create<IThrowSyncService>(svc, new ThrowOnRollbackObserver());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => badProxy.RunAsync());
        Assert.Equal("observer rollback refused", ex.Message);

        // AsyncLocal must be clean — a subsequent call on a normal proxy must succeed.
        var hookSvc = new SimpleAsyncHookService(hooks);
        var goodProxy = TransactionProxyFactory.Create<ISimpleAsyncHookService>(hookSvc, observer: null);
        await goodProxy.RunAndCommitAsync();
        Assert.Equal(["async-hook"], hookSvc.Fired);
    }

    /// <summary>
    /// When a NoRollbackFor exception is thrown, the transaction commits — AfterRollback hooks
    /// must NOT fire because the outcome is CommittedWithException, not RolledBack.
    /// AfterCompletion hooks must still fire regardless of outcome.
    /// </summary>
    [Fact]
    public async Task NoRollbackFor_AfterRollbackHookDoesNotFire()
    {
        var hooks = new TransactionHooks();
        var svc = new NoRollbackWithAfterRollbackHookService(hooks);
        var proxy = TransactionProxyFactory.Create<INoRollbackWithAfterRollbackHookService>(svc, observer: null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => proxy.RunAsync());

        Assert.DoesNotContain("after-rollback", svc.Fired);
        Assert.Contains("after-completion", svc.Fired);
    }

    /// <summary>
    /// Regression: when the method body throws after yielding (async path, not the synchronous preamble),
    /// observer.OnRollback throwing must not prevent ClearScope — AsyncLocal must be restored and
    /// subsequent calls must work on a clean scope.
    /// </summary>
    [Fact]
    public async Task WrapVoidTask_WhenObserverOnRollbackThrows_AsyncLocalRestoredAndExceptionPropagates()
    {
        var hooks = new TransactionHooks();
        var svc = new ThrowAsyncBodyService();
        var badProxy = TransactionProxyFactory.Create<IThrowAsyncBodyService>(svc, new ThrowOnRollbackObserver());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => badProxy.RunAsync());
        Assert.Equal("observer rollback refused", ex.Message);

        // AsyncLocal must be clean — a subsequent call on a normal proxy must succeed.
        var hookSvc = new SimpleAsyncHookService(hooks);
        var goodProxy = TransactionProxyFactory.Create<ISimpleAsyncHookService>(hookSvc, observer: null);
        await goodProxy.RunAndCommitAsync();
        Assert.Equal(["async-hook"], hookSvc.Fired);
    }
}
