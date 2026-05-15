using System.Transactions;
using Gsag.Transactional.Core.Attributes;
using Gsag.Transactional.Core.Hooks;
using Gsag.Transactional.Core.Proxy;
using Xunit;

namespace Gsag.Transactional.Tests.Integration.Hooks;

public interface ISyncHookService
{
    string RunWithAsyncHook();
    string RunSuccess();
    string RunWithFailingFirstSyncHook();
    string RunWithTwoFailingSyncHooks();
    string RunWithSyncCommitAndAsyncCompletionHook();
    string RunWithNoRollbackAndAsyncRollbackHook();
    string RunWithFailingAfterRollbackHook();
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

    [Transactional]
    public string RunWithTwoFailingSyncHooks()
    {
        _hooks.AfterCommit((Action)(() => throw new InvalidOperationException("hook-1 failed")));
        _hooks.AfterCommit((Action)(() => throw new InvalidOperationException("hook-2 failed")));
        return "done";
    }

    [Transactional]
    public string RunWithSyncCommitAndAsyncCompletionHook()
    {
        _hooks.AfterCommit(() => Fired.Add("commit-hook"));
        _hooks.AfterCompletion(async () => await Task.CompletedTask);
        return "done";
    }

    [Transactional(NoRollbackFor = [typeof(InvalidOperationException)])]
    public string RunWithNoRollbackAndAsyncRollbackHook()
    {
        _hooks.AfterRollback(async () => await Task.CompletedTask);
        throw new InvalidOperationException("no-rollback");
    }

    [Transactional]
    public string RunWithFailingAfterRollbackHook()
    {
        _hooks.AfterRollback((Action)(() => throw new InvalidOperationException("hook-failed")));
        throw new InvalidOperationException("business");
    }
}

public interface IAbortWithHookSyncService
{
    string Run();
}

public class AbortWithHookSyncService : IAbortWithHookSyncService
{
    private readonly ITransactionHooks _hooks;
    public List<string> Fired { get; } = [];
    public AbortWithHookSyncService(ITransactionHooks hooks) => _hooks = hooks;

    [Transactional]
    public string Run()
    {
        _hooks.AfterRollback(() => Fired.Add("after-rollback"));
        _hooks.AfterCompletion(() => Fired.Add("after-completion"));
        Transaction.Current!.Rollback();
        return "ok";
    }
}

public class SyncPathHookTests
{
    private static (ISyncHookService proxy, SyncHookService svc) Build()
    {
        var hooks = new TransactionHooks();
        var svc = new SyncHookService(hooks);
        var proxy = TransactionProxyFactory.Create<ISyncHookService>(svc, observer: null);
        return (proxy, svc);
    }

    /// <summary>
    /// Async hooks registered inside a synchronous [Transactional] method are not supported —
    /// the proxy throws NotSupportedException because it cannot await after the method returns.
    /// </summary>
    [Fact]
    public void SyncMethod_WithAsyncHook_ThrowsNotSupportedException()
    {
        var (proxy, _) = Build();

        Assert.Throws<NotSupportedException>(() => proxy.RunWithAsyncHook());
    }

    /// <summary>
    /// After the async-hook guard fires and throws, the AsyncLocal hook slot must be cleared
    /// so that the next call through the same proxy operates on a fresh scope.
    /// </summary>
    [Fact]
    public void SyncMethod_AfterAsyncHookGuardThrows_AsyncLocalIsCleanForSubsequentCall()
    {
        var (proxy, svc) = Build();

        Assert.Throws<NotSupportedException>(() => proxy.RunWithAsyncHook());

        var result = proxy.RunSuccess();
        Assert.Equal("done", result);
        Assert.Equal(["sync-hook"], svc.Fired);
    }

    /// <summary>
    /// Regression for pre-check ordering: if AfterCompletion has an async hook,
    /// NotSupportedException must be thrown before the AfterCommit sync hook executes —
    /// its side-effect must not occur.
    /// </summary>
    [Fact]
    public void SyncMethod_WithAsyncHookOnLaterEvent_PreCheckBlocksAllHooks()
    {
        var (proxy, svc) = Build();

        Assert.Throws<NotSupportedException>(() => proxy.RunWithSyncCommitAndAsyncCompletionHook());

        Assert.Empty(svc.Fired);
    }

    /// <summary>
    /// TriggerSync must run all hooks even when the first one throws,
    /// mirroring the AggregateException guarantee of TriggerAsync.
    /// </summary>
    [Fact]
    public void SyncMethod_WhenFirstSyncHookFails_RemainingHooksStillFire()
    {
        var (proxy, svc) = Build();

        var ex = Assert.Throws<AggregateException>(() => proxy.RunWithFailingFirstSyncHook());

        Assert.Equal(["hook-2", "hook-3"], svc.Fired);
        Assert.Single(ex.InnerExceptions);
        Assert.Equal("hook-1 failed", ex.InnerExceptions[0].Message);
    }

    /// <summary>
    /// Regression: when Scope.Dispose() throws TransactionAbortedException (because
    /// Transaction.Current.Rollback() was called inside the method), hooks registered before
    /// the abort must still fire on the sync [Transactional] path.
    /// </summary>
    [Fact]
    public void TransactionAborted_SyncMethod_WithAfterRollbackHook_HookStillFires()
    {
        var hooks = new TransactionHooks();
        var svc = new AbortWithHookSyncService(hooks);
        var proxy = TransactionProxyFactory.Create<IAbortWithHookSyncService>(svc, observer: null);

        Assert.Throws<TransactionAbortedException>(() => proxy.Run());

        Assert.Contains("after-rollback", svc.Fired);
    }

    [Fact]
    public void TransactionAborted_SyncMethod_WithAfterCompletionHook_HookStillFires()
    {
        var hooks = new TransactionHooks();
        var svc = new AbortWithHookSyncService(hooks);
        var proxy = TransactionProxyFactory.Create<IAbortWithHookSyncService>(svc, observer: null);

        Assert.Throws<TransactionAbortedException>(() => proxy.Run());

        Assert.Contains("after-completion", svc.Fired);
    }

    /// <summary>
    /// On the synchronous call path, async AfterRollback hooks cannot be awaited —
    /// NotSupportedException must be thrown even when the NoRollbackFor path is taken
    /// (i.e., regardless of whether the transaction commits or would have rolled back).
    /// </summary>
    [Fact]
    public void SyncMethod_NoRollbackForPath_WithAsyncRollbackHook_ThrowsNotSupportedException()
    {
        var (proxy, _) = Build();

        Assert.Throws<NotSupportedException>(() => proxy.RunWithNoRollbackAndAsyncRollbackHook());
    }

    /// <summary>
    /// When a sync rollback hook itself throws, the failure must be suppressed so the original
    /// business exception propagates unmasked. Exercises TriggerSync with suppressExceptions=true.
    /// </summary>
    [Fact]
    public void SyncMethod_OnRollbackPath_WhenAfterRollbackHookThrows_OriginalExceptionPropagates()
    {
        var (proxy, _) = Build();

        var ex = Assert.Throws<InvalidOperationException>(
            () => proxy.RunWithFailingAfterRollbackHook());

        Assert.Equal("business", ex.Message);
    }

    /// <summary>
    /// When two sync AfterCommit hooks both fail, the AggregateException must contain both inner
    /// exceptions. Verifies that the error list in TriggerSync uses ??= so earlier exceptions
    /// are not overwritten by later ones.
    /// </summary>
    [Fact]
    public void SyncMethod_WhenTwoSyncHooksFail_BothExceptionsInAggregateException()
    {
        var (proxy, _) = Build();

        var ex = Assert.Throws<AggregateException>(() => proxy.RunWithTwoFailingSyncHooks());

        Assert.Equal(2, ex.InnerExceptions.Count);
    }
}
