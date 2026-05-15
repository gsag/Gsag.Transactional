using Gsag.Transactional.Core.Attributes;
using Gsag.Transactional.Core.Hooks;
using Gsag.Transactional.Core.Proxy;
using Xunit;

namespace Gsag.Transactional.Tests.Integration.Hooks;

public interface IAfterRollbackService
{
    Task CommitAsync();
    Task RollbackAsync();
    Task RollbackWithFailingHookAsync();
    Task RollbackWithTwoFailingHooksAsync();
    ValueTask RollbackValueTaskAsync();
    Task ThrowSyncBeforeTaskAsync();
}

public class AfterRollbackService : IAfterRollbackService
{
    private readonly ITransactionHooks _hooks;
    public List<string> Fired { get; } = [];
    public AfterRollbackService(ITransactionHooks hooks) => _hooks = hooks;

    [Transactional]
    public async Task CommitAsync()
    {
        await Task.CompletedTask;
        _hooks.AfterCommit(() => Fired.Add("after-commit"));
        _hooks.AfterRollback(() => Fired.Add("after-rollback"));
    }

    [Transactional]
    public async Task RollbackAsync()
    {
        _hooks.AfterCommit(() => Fired.Add("after-commit"));
        _hooks.AfterRollback(() => Fired.Add("after-rollback"));
        await Task.CompletedTask;
        throw new InvalidOperationException("forced rollback");
    }

    [Transactional]
    public async Task RollbackWithFailingHookAsync()
    {
        _hooks.AfterRollback((Action)(() => throw new InvalidOperationException("hook-1 failed")));
        _hooks.AfterRollback(() => Fired.Add("hook-2"));
        _hooks.AfterRollback(() => Fired.Add("hook-3"));
        await Task.CompletedTask;
        throw new InvalidOperationException("forced rollback");
    }

    [Transactional]
    public async Task RollbackWithTwoFailingHooksAsync()
    {
        _hooks.AfterRollback((Action)(() =>
        {
            Fired.Add("hook-1-ran");
            throw new InvalidOperationException("hook-1 failed");
        }));
        _hooks.AfterRollback(async () =>
        {
            Fired.Add("hook-2-ran");
            await Task.CompletedTask;
            throw new InvalidOperationException("hook-2 failed");
        });
        await Task.CompletedTask;
        throw new InvalidOperationException("forced rollback");
    }

    [Transactional]
    public async ValueTask RollbackValueTaskAsync()
    {
        _hooks.AfterRollback(() => Fired.Add("after-rollback-valuetask"));
        await Task.CompletedTask;
        throw new InvalidOperationException("forced rollback");
    }

    // NOT async — registers a hook and then throws before returning its Task,
    // exercising the sync-throw-before-task path in HandleAsync.
    [Transactional]
    public Task ThrowSyncBeforeTaskAsync()
    {
        _hooks.AfterRollback(() => Fired.Add("after-rollback-sync-throw"));
        throw new InvalidOperationException("sync before task");
#pragma warning disable CS0162
        return Task.CompletedTask;
#pragma warning restore CS0162
    }
}

public class AfterRollbackTests
{
    private static (IAfterRollbackService proxy, AfterRollbackService svc) Build()
    {
        var hooks = new TransactionHooks();
        var svc = new AfterRollbackService(hooks);
        var proxy = TransactionProxyFactory.Create<IAfterRollbackService>(svc, observer: null);
        return (proxy, svc);
    }

    [Fact]
    public async Task AfterRollback_WhenCommits_DoesNotFire()
    {
        var (proxy, svc) = Build();

        await proxy.CommitAsync();

        Assert.DoesNotContain("after-rollback", svc.Fired);
    }

    [Fact]
    public async Task AfterRollback_WhenRollsBack_Fires()
    {
        var (proxy, svc) = Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() => proxy.RollbackAsync());

        Assert.Contains("after-rollback", svc.Fired);
    }

    /// <summary>
    /// TriggerAsync must run all AfterRollback hooks even when the first one throws.
    /// Hook failures are suppressed on the rollback path — the original rollback exception propagates.
    /// </summary>
    [Fact]
    public async Task AfterRollback_WhenFirstHookFails_RemainingHooksStillFire()
    {
        var (proxy, svc) = Build();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => proxy.RollbackWithFailingHookAsync());

        Assert.Equal("forced rollback", ex.Message);
        Assert.Equal(["hook-2", "hook-3"], svc.Fired);
    }

    /// <summary>
    /// AfterRollback hooks registered in a ValueTask-returning [Transactional] method must fire
    /// when the method throws — verifying the ValueTask async wrapper uses the same hook path.
    /// </summary>
    [Fact]
    public async Task AfterRollback_ValueTaskVoid_WhenRollsBack_Fires()
    {
        var (proxy, svc) = Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() => proxy.RollbackValueTaskAsync().AsTask());

        Assert.Contains("after-rollback-valuetask", svc.Fired);
    }

    /// <summary>
    /// Regression: a Task-returning method that registers an AfterRollback hook and then throws
    /// synchronously before returning its task must still fire the hook. The proxy converts the
    /// sync throw to a pre-faulted Task so the normal async wrapper runs the full rollback lifecycle.
    /// </summary>
    [Fact]
    public async Task AfterRollback_WhenMethodThrowsSyncBeforeReturningTask_Fires()
    {
        var (proxy, svc) = Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() => proxy.ThrowSyncBeforeTaskAsync());

        Assert.Contains("after-rollback-sync-throw", svc.Fired);
    }

    /// <summary>
    /// When two AfterRollback hooks both fail, the AggregateException must contain both inner
    /// exceptions. Verifies that the error list uses ??= so earlier exceptions are not lost.
    /// Hook failures are suppressed on the rollback path — the original exception propagates.
    /// </summary>
    [Fact]
    public async Task AfterRollback_WhenTwoHooksFail_BothExceptionsCollectedButSuppressed()
    {
        var (proxy, svc) = Build();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => proxy.RollbackWithTwoFailingHooksAsync());

        Assert.Equal("forced rollback", ex.Message);
        Assert.Equal(["hook-1-ran", "hook-2-ran"], svc.Fired);
    }
}
