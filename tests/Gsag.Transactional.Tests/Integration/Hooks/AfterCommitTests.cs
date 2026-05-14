using Gsag.Transactional.Core.Attributes;
using Gsag.Transactional.Core.Hooks;
using Gsag.Transactional.Core.Proxy;
using Xunit;

namespace Gsag.Transactional.Tests.Integration.Hooks;

public interface IAfterCommitService
{
    Task RunAndCommitAsync();
    Task RunAndRollbackAsync();
    Task RunWithSyncHookAsync();
    Task RunWithFailingHookAsync();
}

public class AfterCommitService : IAfterCommitService
{
    private readonly ITransactionHooks _hooks;
    public List<string> Fired { get; } = [];
    public AfterCommitService(ITransactionHooks hooks) => _hooks = hooks;

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

public class AfterCommitTests
{
    private static (IAfterCommitService proxy, AfterCommitService svc) Build()
    {
        var hooks = new TransactionHooks();
        var svc = new AfterCommitService(hooks);
        var proxy = TransactionProxyFactory.Create<IAfterCommitService>(svc, observer: null);
        return (proxy, svc);
    }

    [Fact]
    public async Task AfterCommit_WhenSucceeds_AsyncHookFires()
    {
        var (proxy, svc) = Build();

        await proxy.RunAndCommitAsync();

        Assert.Equal(["async-hook"], svc.Fired);
    }

    [Fact]
    public async Task AfterCommit_WhenRollsBack_HookDoesNotFire()
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

    [Fact]
    public void AfterCommit_OutsideAnyScope_IsNoOp()
    {
        var hooks = new TransactionHooks();

        hooks.AfterCommit((Action)(() => throw new Exception("should not fire")));
        hooks.AfterCommit(async () => { await Task.CompletedTask; throw new Exception("should not fire"); });

        // No exception thrown — registrations outside a scope are silently dropped.
    }
}
