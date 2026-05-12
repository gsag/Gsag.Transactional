using Gsag.Transactional.Core.Attributes;
using Gsag.Transactional.Core.Hooks;
using Gsag.Transactional.Core.Proxy;
using Xunit;

namespace Gsag.Transactional.Tests.Integration.Hooks;

public interface IBeforeRollbackService
{
    Task RunAndRollbackAsync();
    Task RunAndCommitAsync();
    Task RunWithNoRollbackForAsync();
    Task RunWithFailingBeforeRollbackHookAsync();
    ValueTask RunValueTaskAndRollbackAsync();
    void RunSyncAndRollback();
    void RunSyncWithAsyncBeforeRollbackHook();
}

public class BeforeRollbackService : IBeforeRollbackService
{
    private readonly ITransactionHooks _hooks;
    public List<string> Fired { get; } = [];
    public BeforeRollbackService(ITransactionHooks hooks) => _hooks = hooks;

    [Transactional]
    public async Task RunAndRollbackAsync()
    {
        _hooks.BeforeRollback(async () =>
        {
            await Task.CompletedTask;
            Fired.Add("before-rollback");
        });
        _hooks.AfterRollback(() => Fired.Add("after-rollback"));
        await Task.CompletedTask;
        throw new InvalidOperationException("forced rollback");
    }

    [Transactional]
    public async Task RunAndCommitAsync()
    {
        _hooks.BeforeRollback(() => Fired.Add("should-not-fire"));
        await Task.CompletedTask;
    }

    [Transactional(NoRollbackFor = [typeof(InvalidOperationException)])]
    public async Task RunWithNoRollbackForAsync()
    {
        _hooks.BeforeRollback(() => Fired.Add("should-not-fire"));
        await Task.CompletedTask;
        throw new InvalidOperationException("no-rollback exception");
    }

    [Transactional]
    public async Task RunWithFailingBeforeRollbackHookAsync()
    {
        _hooks.BeforeRollback(() => throw new Exception("hook failure"));
        _hooks.AfterRollback(() => Fired.Add("after-rollback"));
        await Task.CompletedTask;
        throw new InvalidOperationException("forced rollback");
    }

    [Transactional]
    public void RunSyncAndRollback()
    {
        _hooks.BeforeRollback(() => Fired.Add("before-rollback"));
        _hooks.AfterRollback(() => Fired.Add("after-rollback"));
        throw new InvalidOperationException("forced rollback");
    }

    [Transactional]
    public async ValueTask RunValueTaskAndRollbackAsync()
    {
        _hooks.BeforeRollback(async () =>
        {
            await Task.CompletedTask;
            Fired.Add("before-rollback");
        });
        _hooks.AfterRollback(() => Fired.Add("after-rollback"));
        await Task.CompletedTask;
        throw new InvalidOperationException("forced rollback");
    }

    [Transactional]
    public void RunSyncWithAsyncBeforeRollbackHook()
    {
        _hooks.BeforeRollback(async () => { await Task.CompletedTask; Fired.Add("async-hook"); });
        throw new InvalidOperationException("forced rollback");
    }
}

public class BeforeRollbackTests
{
    private static (IBeforeRollbackService proxy, BeforeRollbackService svc) Build()
    {
        var hooks = new TransactionHooks();
        var svc   = new BeforeRollbackService(hooks);
        var proxy = TransactionProxyFactory.Create<IBeforeRollbackService>(svc, observer: null);
        return (proxy, svc);
    }

    [Fact]
    public async Task BeforeRollback_OnRollback_AsyncHookFires()
    {
        var (proxy, svc) = Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() => proxy.RunAndRollbackAsync());

        Assert.Contains("before-rollback", svc.Fired);
        Assert.Contains("after-rollback", svc.Fired);
        Assert.Equal("before-rollback", svc.Fired[0]);
        Assert.Equal("after-rollback", svc.Fired[1]);
    }

    [Fact]
    public async Task BeforeRollback_OnSuccess_DoesNotFire()
    {
        var (proxy, svc) = Build();

        await proxy.RunAndCommitAsync();

        Assert.DoesNotContain("should-not-fire", svc.Fired);
    }

    [Fact]
    public async Task BeforeRollback_WhenHookThrows_ExceptionIsSuppressed()
    {
        var (proxy, svc) = Build();

        // Original InvalidOperationException propagates; hook failure is suppressed.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => proxy.RunWithFailingBeforeRollbackHookAsync());

        Assert.Equal("forced rollback", ex.Message);
        Assert.Contains("after-rollback", svc.Fired); // AfterRollback still fires
    }

    [Fact]
    public void BeforeRollback_SyncPath_SyncHookFires()
    {
        var (proxy, svc) = Build();

        Assert.Throws<InvalidOperationException>(() => proxy.RunSyncAndRollback());

        Assert.Contains("before-rollback", svc.Fired);
        Assert.Contains("after-rollback", svc.Fired);
        Assert.Equal("before-rollback", svc.Fired[0]);
        Assert.Equal("after-rollback", svc.Fired[1]);
    }

    [Fact]
    public async Task BeforeRollback_OnNoRollbackFor_DoesNotFire()
    {
        var (proxy, svc) = Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() => proxy.RunWithNoRollbackForAsync());

        Assert.DoesNotContain("should-not-fire", svc.Fired);
    }

    [Fact]
    public async Task BeforeRollback_ValueTask_OnRollback_AsyncHookFires()
    {
        var (proxy, svc) = Build();

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await proxy.RunValueTaskAndRollbackAsync());

        Assert.Contains("before-rollback", svc.Fired);
        Assert.Contains("after-rollback", svc.Fired);
        Assert.Equal("before-rollback", svc.Fired[0]);
        Assert.Equal("after-rollback", svc.Fired[1]);
    }

    [Fact]
    public void BeforeRollback_AsyncHookInSyncMethod_ThrowsNotSupported()
    {
        var (proxy, _) = Build();

        Assert.Throws<NotSupportedException>(() => proxy.RunSyncWithAsyncBeforeRollbackHook());
    }
}
