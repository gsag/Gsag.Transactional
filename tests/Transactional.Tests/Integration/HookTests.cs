using System.Reflection;
using System.Transactions;
using Transactional.Core.Attributes;
using Transactional.Core.Hooks;
using Transactional.Core.Observability;
using Transactional.Core.Proxy;
using Xunit;

namespace Transactional.Tests.Integration;

// ---------------------------------------------------------------------------
// Test doubles
// ---------------------------------------------------------------------------

public interface IHookDemoService
{
    Task RunAndCommitAsync();
    Task RunAndRollbackAsync();
    Task RunWithSyncHookAsync();
    Task RunWithFailingHookAsync();
}

public class HookDemoService : IHookDemoService
{
    private readonly ITransactionHooks _hooks;

    public List<string> Fired { get; } = [];

    public HookDemoService(ITransactionHooks hooks) => _hooks = hooks;

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

    [Transactional]
    public async Task RunAndRollbackAsync()
    {
        _hooks.AfterCommit(async () =>
        {
            await Task.CompletedTask;
            Fired.Add("should-not-fire");
        });
        await Task.CompletedTask;
        throw new InvalidOperationException("forced rollback");
    }

    [Transactional]
    public async Task RunWithSyncHookAsync()
    {
        await Task.CompletedTask;
        _hooks.AfterCommit(() => Fired.Add("sync-hook"));
    }

    [Transactional]
    public async Task RunWithFailingHookAsync()
    {
        await Task.CompletedTask;
        _hooks.AfterCommit(() => Fired.Add("hook-1"));
        _hooks.AfterCommit(async () =>
        {
            await Task.CompletedTask;
            throw new InvalidOperationException("hook-2 failed");
        });
        _hooks.AfterCommit(() => Fired.Add("hook-3"));
    }
}

// ---------------------------------------------------------------------------
// Test doubles for RequiresNew nesting test
// ---------------------------------------------------------------------------

public interface IHookInnerService
{
    Task RunAsync();
}

public class HookInnerService : IHookInnerService
{
    private readonly ITransactionHooks _hooks;
    public List<string> Fired { get; } = [];
    public HookInnerService(ITransactionHooks hooks) => _hooks = hooks;

    [Transactional(Propagation = TransactionScopeOption.RequiresNew)]
    public async Task RunAsync()
    {
        await Task.CompletedTask;
        _hooks.AfterCommit(() => Fired.Add("inner-hook"));
    }
}

public interface IHookOuterService
{
    Task RunAsync();
}

public class HookOuterService : IHookOuterService
{
    private readonly ITransactionHooks _hooks;
    private readonly IHookInnerService _inner;
    public List<string> Fired { get; } = [];

    public HookOuterService(ITransactionHooks hooks, IHookInnerService inner)
    {
        _hooks = hooks;
        _inner = inner;
    }

    [Transactional]
    public async Task RunAsync()
    {
        _hooks.AfterCommit(() => Fired.Add("outer-hook-before"));
        await _inner.RunAsync();
        // Without the BeginScope restore fix this hook would be silently dropped:
        // the inner RequiresNew scope clobbers _current.Value and ClearScope nulls it.
        _hooks.AfterCommit(() => Fired.Add("outer-hook-after"));
    }
}

// ---------------------------------------------------------------------------
// Test doubles for Suppress nesting test
// ---------------------------------------------------------------------------

public interface ISuppressService
{
    Task RunSuppressedAsync();
}

public class SuppressService : ISuppressService
{
    private readonly ITransactionHooks _hooks;
    public List<string> Fired { get; } = [];
    public SuppressService(ITransactionHooks hooks) => _hooks = hooks;

    [Transactional(Propagation = TransactionScopeOption.Suppress)]
    public async Task RunSuppressedAsync()
    {
        await Task.CompletedTask;
        // _current is null inside Suppress — this is a no-op by design.
        _hooks.AfterCommit(() => Fired.Add("suppress-hook"));
    }
}

public interface ISuppressOuterService
{
    Task RunAsync();
}

public class SuppressOuterService : ISuppressOuterService
{
    private readonly ITransactionHooks _hooks;
    private readonly ISuppressService _inner;
    public List<string> Fired { get; } = [];

    public SuppressOuterService(ITransactionHooks hooks, ISuppressService inner)
    {
        _hooks = hooks;
        _inner = inner;
    }

    [Transactional]
    public async Task RunAsync()
    {
        _hooks.AfterCommit(() => Fired.Add("outer-hook-before"));
        await _inner.RunSuppressedAsync();
        // Without the Suppress restore fix, _current is left null here and this hook is lost.
        _hooks.AfterCommit(() => Fired.Add("outer-hook-after"));
    }
}

// ---------------------------------------------------------------------------
// Test doubles for sync [Transactional] tests
// ---------------------------------------------------------------------------

public interface ISyncHookService
{
    string RunWithAsyncHook();
    string RunSuccess();
    string RunWithFailingFirstSyncHook();
}

public class SyncHookService : ISyncHookService
{
    private readonly ITransactionHooks _hooks;
    public List<string> Fired { get; } = [];
    public SyncHookService(ITransactionHooks hooks) => _hooks = hooks;

    [Transactional]
    public string RunWithAsyncHook()
    {
        _hooks.AfterCommit(async () => await Task.CompletedTask);
        return "done";
    }

    [Transactional]
    public string RunSuccess()
    {
        _hooks.AfterCommit(() => Fired.Add("sync-hook"));
        return "done";
    }

    [Transactional]
    public string RunWithFailingFirstSyncHook()
    {
        // Explicit cast avoids ambiguity: a throw-only lambda is valid for both Action and
        // Func<Task> (all code paths terminate), so the compiler can't pick without a hint.
        _hooks.AfterCommit((Action)(() => throw new InvalidOperationException("hook-1 failed")));
        _hooks.AfterCommit(() => Fired.Add("hook-2"));
        _hooks.AfterCommit(() => Fired.Add("hook-3"));
        return "done";
    }
}

// ---------------------------------------------------------------------------
// Test doubles for NoRollbackFor + hook masking test
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
// Test doubles for observer OnBegin throws test
// ---------------------------------------------------------------------------

public class ThrowOnBeginObserver : ITransactionLifecycleObserver
{
    public void OnBegin(MethodInfo method, TransactionalAttribute attr) =>
        throw new InvalidOperationException("observer refused");

    public void OnCommit(MethodInfo method, TimeSpan elapsed) { }
    public void OnRollback(MethodInfo method, Exception exception, TimeSpan elapsed) { }
}

public class EventRecordingObserver : ITransactionLifecycleObserver
{
    public List<string> Events { get; } = [];

    public void OnBegin(MethodInfo method, TransactionalAttribute attr) =>
        Events.Add("begin");

    public void OnCommit(MethodInfo method, TimeSpan elapsed) =>
        Events.Add("commit");

    public void OnRollback(MethodInfo method, Exception exception, TimeSpan elapsed) =>
        Events.Add($"rollback:{exception.GetType().Name}");
}

// ---------------------------------------------------------------------------
// Test doubles for Scope.Complete() throwing test
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

// ---------------------------------------------------------------------------
// Test doubles for ValueTask<T> + NoRollbackFor test
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
// Test doubles for Suppress + RequiresNew three-level nesting test
// ---------------------------------------------------------------------------

public interface IRequiresNewInSuppressService
{
    Task RunAsync();
}

public class RequiresNewInSuppressService : IRequiresNewInSuppressService
{
    private readonly ITransactionHooks _hooks;
    public List<string> Fired { get; } = [];
    public RequiresNewInSuppressService(ITransactionHooks hooks) => _hooks = hooks;

    [Transactional(Propagation = TransactionScopeOption.RequiresNew)]
    public async Task RunAsync()
    {
        await Task.CompletedTask;
        _hooks.AfterCommit(() => Fired.Add("requiresnew-hook"));
    }
}

public interface ISuppressWithRequiresNewService
{
    Task RunAsync();
}

public class SuppressWithRequiresNewService : ISuppressWithRequiresNewService
{
    private readonly IRequiresNewInSuppressService _inner;
    public SuppressWithRequiresNewService(IRequiresNewInSuppressService inner) => _inner = inner;

    [Transactional(Propagation = TransactionScopeOption.Suppress)]
    public async Task RunAsync()
    {
        await _inner.RunAsync();
    }
}

public interface IOuterWithSuppressAndRequiresNewService
{
    Task RunAsync();
}

public class OuterWithSuppressAndRequiresNewService : IOuterWithSuppressAndRequiresNewService
{
    private readonly ITransactionHooks _hooks;
    private readonly ISuppressWithRequiresNewService _mid;
    public List<string> Fired { get; } = [];

    public OuterWithSuppressAndRequiresNewService(ITransactionHooks hooks, ISuppressWithRequiresNewService mid)
    {
        _hooks = hooks;
        _mid = mid;
    }

    [Transactional]
    public async Task RunAsync()
    {
        _hooks.AfterCommit(() => Fired.Add("outer-hook-before"));
        await _mid.RunAsync();
        _hooks.AfterCommit(() => Fired.Add("outer-hook-after"));
    }
}

// ---------------------------------------------------------------------------

/// <summary>
/// Verifies the ITransactionHooks lifecycle: hooks fire after commit,
/// are discarded on rollback, and all run even if one throws.
/// Uses TransactionHooks directly (enabled by InternalsVisibleTo in Transactional.Core.csproj).
/// </summary>
public class HookTests
{
    private static (IHookDemoService proxy, HookDemoService svc) Build()
    {
        var hooks = new TransactionHooks();
        var svc   = new HookDemoService(hooks);
        var proxy = TransactionProxyFactory.Create<IHookDemoService>(svc, observer: null);
        return (proxy, svc);
    }

    [Fact]
    public async Task AfterCommit_OnSuccess_AsyncHookFires()
    {
        var (proxy, svc) = Build();

        await proxy.RunAndCommitAsync();

        Assert.Equal(["async-hook"], svc.Fired);
    }

    [Fact]
    public async Task AfterCommit_OnRollback_HookNotFired()
    {
        var (proxy, svc) = Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() => proxy.RunAndRollbackAsync());

        Assert.Empty(svc.Fired);
    }

    [Fact]
    public async Task AfterCommit_SyncHook_FiresInAsyncMethod()
    {
        var (proxy, svc) = Build();

        await proxy.RunWithSyncHookAsync();

        Assert.Equal(["sync-hook"], svc.Fired);
    }

    [Fact]
    public async Task AfterCommit_WhenOneHookFails_RemainingHooksStillFire()
    {
        var (proxy, svc) = Build();

        var ex = await Assert.ThrowsAsync<AggregateException>(() => proxy.RunWithFailingHookAsync());

        // hook-1 (sync) and hook-3 (sync) fire; hook-2 (async) throws
        Assert.Equal(["hook-1", "hook-3"], svc.Fired);
        Assert.Single(ex.InnerExceptions);
        Assert.Equal("hook-2 failed", ex.InnerExceptions[0].Message);
    }

    /// <summary>
    /// Regression: RequiresNew overwrote _current.Value without restoring it.
    /// Hooks registered on the outer scope after the inner RequiresNew returned
    /// were silently dropped because _current.Value was null.
    /// </summary>
    [Fact]
    public async Task RequiresNew_BothScopesRegisterHooks_BothHooksFire()
    {
        var hooks     = new TransactionHooks();
        var innerSvc  = new HookInnerService(hooks);
        var innerProxy = TransactionProxyFactory.Create<IHookInnerService>(innerSvc, observer: null);
        var outerSvc  = new HookOuterService(hooks, innerProxy);
        var outerProxy = TransactionProxyFactory.Create<IHookOuterService>(outerSvc, observer: null);

        await outerProxy.RunAsync();

        // Inner hook fires when the RequiresNew scope commits (inside outerProxy.RunAsync).
        Assert.Equal(["inner-hook"], innerSvc.Fired);
        // Both outer hooks fire when the outer Required scope commits.
        // outer-hook-after would be lost without the BeginScope restore fix.
        Assert.Equal(["outer-hook-before", "outer-hook-after"], outerSvc.Fired);
    }

    /// <summary>
    /// Regression: Suppress set _current.Value = null without restoring Previous.
    /// Hooks registered on the outer scope after the Suppress call returned were dropped.
    /// </summary>
    [Fact]
    public async Task Suppress_HooksInOuterScopeAroundSuppressedCall_AllOuterHooksFire()
    {
        var hooks        = new TransactionHooks();
        var innerSvc     = new SuppressService(hooks);
        var innerProxy   = TransactionProxyFactory.Create<ISuppressService>(innerSvc, observer: null);
        var outerSvc     = new SuppressOuterService(hooks, innerProxy);
        var outerProxy   = TransactionProxyFactory.Create<ISuppressOuterService>(outerSvc, observer: null);

        await outerProxy.RunAsync();

        // Hook registered inside the Suppress scope is a no-op — _current is null there.
        Assert.Empty(innerSvc.Fired);
        // Both outer hooks fire; outer-hook-after would be lost without the Suppress restore fix.
        Assert.Equal(["outer-hook-before", "outer-hook-after"], outerSvc.Fired);
    }

    /// <summary>
    /// EnsureNoAsyncHooks must fire when an async hook is registered inside a sync [Transactional] method.
    /// Verifies the guard runs after scope dispose (no AsyncLocal leak) so the next call works normally.
    /// </summary>
    [Fact]
    public void SyncMethod_WithAsyncHook_ThrowsNotSupportedException()
    {
        var hooks = new TransactionHooks();
        var svc   = new SyncHookService(hooks);
        var proxy = TransactionProxyFactory.Create<ISyncHookService>(svc, observer: null);

        Assert.Throws<NotSupportedException>(() => proxy.RunWithAsyncHook());

        // AsyncLocal must be cleared — a subsequent call with only sync hooks must succeed.
        var result = proxy.RunSuccess();
        Assert.Equal("done", result);
        Assert.Equal(["sync-hook"], svc.Fired);
    }

    /// <summary>
    /// TriggerSync must run all hooks even when the first one throws,
    /// mirroring the AggregateException guarantee of TriggerAsync.
    /// </summary>
    [Fact]
    public void SyncMethod_WhenFirstSyncHookFails_RemainingHooksStillFire()
    {
        var hooks = new TransactionHooks();
        var svc   = new SyncHookService(hooks);
        var proxy = TransactionProxyFactory.Create<ISyncHookService>(svc, observer: null);

        var ex = Assert.Throws<AggregateException>(() => proxy.RunWithFailingFirstSyncHook());

        Assert.Equal(["hook-2", "hook-3"], svc.Fired);
        Assert.Single(ex.InnerExceptions);
        Assert.Equal("hook-1 failed", ex.InnerExceptions[0].Message);
    }

    /// <summary>
    /// When a NoRollbackFor exception is in-flight and a hook also throws,
    /// the hook's AggregateException must not replace the original business exception.
    /// </summary>
    [Fact]
    public async Task NoRollbackFor_WhenHookThrows_BusinessExceptionIsNotMasked()
    {
        var hooks = new TransactionHooks();
        var svc   = new NoRollbackHookService(hooks);
        var proxy = TransactionProxyFactory.Create<INoRollbackHookService>(svc, observer: null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => proxy.RunAsync());

        Assert.Equal("business error", ex.Message);
    }

    /// <summary>
    /// If observer.OnBegin throws, OpenScope must restore the AsyncLocal and propagate the exception.
    /// A subsequent call through a normal proxy must succeed.
    /// </summary>
    [Fact]
    public async Task OpenScope_WhenObserverOnBeginThrows_AsyncLocalRestoredAndExceptionPropagates()
    {
        var hooks    = new TransactionHooks();
        var svc      = new HookDemoService(hooks);
        var badProxy = TransactionProxyFactory.Create<IHookDemoService>(svc, new ThrowOnBeginObserver());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => badProxy.RunAndCommitAsync());
        Assert.Equal("observer refused", ex.Message);

        // AsyncLocal must be clean — a subsequent call on a normal proxy must succeed.
        var goodProxy = TransactionProxyFactory.Create<IHookDemoService>(svc, observer: null);
        await goodProxy.RunAndCommitAsync();
        Assert.Equal(["async-hook"], svc.Fired);
    }

    /// <summary>
    /// When Rollback() is called inside a [Transactional] method, Scope.Dispose() throws
    /// TransactionAbortedException. Complete() was called first and OnCommit fired; Dispose
    /// then throws. Verifies the exception propagates to the caller and that ClearScope ran
    /// (AsyncLocal clean) so subsequent calls work normally.
    ///
    /// Note: Scope.Complete() itself can also throw in distributed-transaction scenarios
    /// (DependentTransaction already committed/aborted). Our Commit() helper wraps Complete()
    /// with a try/catch that fires OnRollback in that case — tested separately.
    /// </summary>
    [Fact]
    public async Task TransactionAborted_WhenRollbackCalledInsideMethod_ExceptionPropagatesAndScopeIsClean()
    {
        var hooks  = new TransactionHooks();
        var svc    = new AbortBeforeCompleteService();
        var proxy  = TransactionProxyFactory.Create<IAbortBeforeCompleteService>(svc, observer: null);

        await Assert.ThrowsAsync<TransactionAbortedException>(() => proxy.RunAsync());

        // ClearScope must have run despite Dispose() throwing — subsequent call must succeed.
        var hookSvc   = new HookDemoService(hooks);
        var hookProxy = TransactionProxyFactory.Create<IHookDemoService>(hookSvc, observer: null);
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
        var svc   = new ValueTaskNoRollbackService(hooks);
        var proxy = TransactionProxyFactory.Create<IValueTaskNoRollbackService>(svc, observer: null);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await proxy.RunAsync());

        Assert.Equal(["hook"], svc.Fired);
    }

    /// <summary>
    /// AfterCommit called outside any [Transactional] scope (_current is null) must be a no-op
    /// and must not throw.
    /// </summary>
    [Fact]
    public void AfterCommit_OutsideAnyScope_IsNoOp()
    {
        var hooks = new TransactionHooks();

        hooks.AfterCommit((Action)(() => throw new Exception("should not fire")));
        hooks.AfterCommit(async () => { await Task.CompletedTask; throw new Exception("should not fire"); });

        // No exception thrown — registrations outside a scope are silently dropped.
    }

    /// <summary>
    /// Three-level nesting: Required (outer) → Suppress (mid) → RequiresNew (inner).
    /// The outer hooks registered around the Suppress+RequiresNew chain must still fire.
    /// </summary>
    [Fact]
    public async Task Suppress_ContainingRequiresNew_OuterHooksStillFire()
    {
        var hooks      = new TransactionHooks();
        var innerSvc   = new RequiresNewInSuppressService(hooks);
        var innerProxy = TransactionProxyFactory.Create<IRequiresNewInSuppressService>(innerSvc, observer: null);
        var midSvc     = new SuppressWithRequiresNewService(innerProxy);
        var midProxy   = TransactionProxyFactory.Create<ISuppressWithRequiresNewService>(midSvc, observer: null);
        var outerSvc   = new OuterWithSuppressAndRequiresNewService(hooks, midProxy);
        var outerProxy = TransactionProxyFactory.Create<IOuterWithSuppressAndRequiresNewService>(outerSvc, observer: null);

        await outerProxy.RunAsync();

        // Inner RequiresNew hook fires when its independent scope commits (inside the Suppress wrapper).
        Assert.Equal(["requiresnew-hook"], innerSvc.Fired);
        // Both outer hooks fire when the outer Required scope commits.
        // outer-hook-after would be lost if the Suppress+RequiresNew stack corrupted the AsyncLocal.
        Assert.Equal(["outer-hook-before", "outer-hook-after"], outerSvc.Fired);
    }
}
