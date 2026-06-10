using Gsag.Transactional.Core.Attributes;
using Gsag.Transactional.Core.Hooks;
using Gsag.Transactional.Core.Proxy;
using Gsag.Transactional.Tests.Core.Unit;
using Xunit;

namespace Gsag.Transactional.Tests.Core.Integration.Hooks;

public interface IBeforeCommitService
{
    Task RunAndCommitAsync();
    Task RunAndRollbackAsync();
    Task RunWithNoRollbackForAsync();
    Task RunWithNoRollbackForAndFailingHookAsync();
    Task RunWithFailingBeforeCommitHookAsync();
    void RunSyncAndCommit();
    void RunSyncWithFailingBeforeCommitHook();
    void RunSyncWithAsyncBeforeCommitHook();
    void RunSyncWithNoRollbackFor();
    void RunSyncWithNoRollbackForAndFailingHook();
}

public class BeforeCommitService : IBeforeCommitService
{
    private readonly ITransactionHooks _hooks;
    public List<string> Fired { get; } = [];
    public BeforeCommitService(ITransactionHooks hooks) => _hooks = hooks;

    [Transactional]
    public async Task RunAndCommitAsync()
    {
        await Task.CompletedTask;
        _hooks.BeforeCommit(async () =>
        {
            await Task.CompletedTask;
            Fired.Add("before-commit");
        });
        _hooks.AfterCommit(() => Fired.Add("after-commit"));
    }

    [Transactional]
    public async Task RunAndRollbackAsync()
    {
        _hooks.BeforeCommit(() => Fired.Add("should-not-fire"));
        await Task.CompletedTask;
        throw new InvalidOperationException("forced rollback");
    }

    [Transactional(NoRollbackFor = [typeof(InvalidOperationException)])]
    public async Task RunWithNoRollbackForAsync()
    {
        _hooks.BeforeCommit(() => Fired.Add("before-commit"));
        _hooks.AfterCommit(() => Fired.Add("after-commit"));
        await Task.CompletedTask;
        throw new InvalidOperationException("no-rollback exception");
    }

    [Transactional(NoRollbackFor = [typeof(InvalidOperationException)])]
    public async Task RunWithNoRollbackForAndFailingHookAsync()
    {
        _hooks.BeforeCommit(() => throw new Exception("hook failure"));
        _hooks.AfterCommit(() => Fired.Add("after-commit"));
        await Task.CompletedTask;
        throw new InvalidOperationException("no-rollback exception");
    }

    [Transactional]
    public async Task RunWithFailingBeforeCommitHookAsync()
    {
        await Task.CompletedTask;
        _hooks.BeforeCommit(() => throw new InvalidOperationException("before-commit failure"));
        _hooks.AfterCommit(() => Fired.Add("should-not-fire"));
        _hooks.AfterRollback(() => Fired.Add("after-rollback"));
    }

    [Transactional]
    public void RunSyncAndCommit()
    {
        _hooks.BeforeCommit(() => Fired.Add("before-commit"));
        _hooks.AfterCommit(() => Fired.Add("after-commit"));
    }

    [Transactional]
    public void RunSyncWithFailingBeforeCommitHook()
    {
        Action failHook = () => throw new InvalidOperationException("before-commit failure");
        _hooks.BeforeCommit(failHook);
        _hooks.AfterCommit(() => Fired.Add("should-not-fire"));
        _hooks.AfterRollback(() => Fired.Add("after-rollback"));
    }

    [Transactional]
    public void RunSyncWithAsyncBeforeCommitHook()
    {
        _hooks.BeforeCommit(async () => { await Task.CompletedTask; Fired.Add("async-hook"); });
    }

    [Transactional(NoRollbackFor = [typeof(InvalidOperationException)])]
    public void RunSyncWithNoRollbackFor()
    {
        _hooks.BeforeCommit(() => Fired.Add("before-commit"));
        _hooks.AfterCommit(() => Fired.Add("after-commit"));
        throw new InvalidOperationException("no-rollback exception");
    }

    [Transactional(NoRollbackFor = [typeof(InvalidOperationException)])]
    public void RunSyncWithNoRollbackForAndFailingHook()
    {
        Action hookThatThrows = () => throw new Exception("hook failure");
        _hooks.BeforeCommit(hookThatThrows);
        _hooks.AfterCommit(() => Fired.Add("after-commit"));
        throw new InvalidOperationException("no-rollback exception");
    }
}

public class BeforeCommitTests
{
    private static (IBeforeCommitService proxy, BeforeCommitService svc) Build()
    {
        var hooks = new TransactionHooks();
        var svc = new BeforeCommitService(hooks);
        var proxy = TransactionProxyFactory.Create<IBeforeCommitService>(svc, observer: null);
        return (proxy, svc);
    }

    private static (IBeforeCommitService proxy, BeforeCommitService svc, RecordingObserver observer) BuildWithObserver()
    {
        var hooks = new TransactionHooks();
        var svc = new BeforeCommitService(hooks);
        var observer = new RecordingObserver();
        var proxy = TransactionProxyFactory.Create<IBeforeCommitService>(svc, observer);
        return (proxy, svc, observer);
    }

    [Fact]
    public async Task BeforeCommit_WhenSucceeds_AsyncHookFires()
    {
        var (proxy, svc) = Build();

        await proxy.RunAndCommitAsync();

        Assert.Contains("before-commit", svc.Fired);
        Assert.Contains("after-commit", svc.Fired);
        Assert.Equal("before-commit", svc.Fired[0]);
        Assert.Equal("after-commit", svc.Fired[1]);
    }

    [Fact]
    public async Task BeforeCommit_WhenRollsBack_DoesNotFire()
    {
        var (proxy, svc) = Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() => proxy.RunAndRollbackAsync());

        Assert.DoesNotContain("should-not-fire", svc.Fired);
    }

    [Fact]
    public async Task BeforeCommit_WhenNoRollbackFor_Fires()
    {
        var (proxy, svc) = Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() => proxy.RunWithNoRollbackForAsync());

        Assert.Contains("before-commit", svc.Fired);
        Assert.Contains("after-commit", svc.Fired);
    }

    [Fact]
    public async Task BeforeCommit_WhenNoRollbackFor_AndHookThrows_ExceptionIsSuppressed()
    {
        var (proxy, svc) = Build();

        // Original InvalidOperationException (NoRollbackFor) propagates; hook failure is suppressed.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => proxy.RunWithNoRollbackForAndFailingHookAsync());

        Assert.Equal("no-rollback exception", ex.Message);
        Assert.Contains("after-commit", svc.Fired); // transaction still committed
    }

    [Fact]
    public async Task BeforeCommit_WhenHookThrows_TransactionRollsBack()
    {
        var (proxy, svc) = Build();

        var ex = await Assert.ThrowsAsync<AggregateException>(
            () => proxy.RunWithFailingBeforeCommitHookAsync());

        Assert.Contains("before-commit failure", ex.InnerExceptions[0].Message);
        Assert.DoesNotContain("should-not-fire", svc.Fired); // AfterCommit must not fire
        Assert.Contains("after-rollback", svc.Fired);        // AfterRollback must fire
    }

    [Fact]
    public void BeforeCommit_SyncPath_SyncHookFires()
    {
        var (proxy, svc) = Build();

        proxy.RunSyncAndCommit();

        Assert.Contains("before-commit", svc.Fired);
        Assert.Contains("after-commit", svc.Fired);
        Assert.Equal("before-commit", svc.Fired[0]);
        Assert.Equal("after-commit", svc.Fired[1]);
    }

    [Fact]
    public void BeforeCommit_SyncPath_WhenHookThrows_TransactionRollsBack()
    {
        var (proxy, svc) = Build();

        var ex = Assert.Throws<AggregateException>(() => proxy.RunSyncWithFailingBeforeCommitHook());

        Assert.Contains("before-commit failure", ex.InnerExceptions[0].Message);
        Assert.DoesNotContain("should-not-fire", svc.Fired);
        Assert.Contains("after-rollback", svc.Fired);
    }

    [Fact]
    public void BeforeCommit_AsyncHookInSyncMethod_ThrowsNotSupported()
    {
        var (proxy, _) = Build();

        Assert.Throws<NotSupportedException>(() => proxy.RunSyncWithAsyncBeforeCommitHook());
    }

    // Sync NoRollbackFor path — BeforeCommit hooks run with suppressExceptions: true (SyncHandler lines 37-40)

    [Fact]
    public void BeforeCommit_SyncPath_WhenNoRollbackFor_Fires()
    {
        var (proxy, svc) = Build();

        Assert.Throws<InvalidOperationException>(() => proxy.RunSyncWithNoRollbackFor());

        Assert.Contains("before-commit", svc.Fired);
        Assert.Contains("after-commit", svc.Fired);
    }

    [Fact]
    public void BeforeCommit_SyncPath_WhenNoRollbackFor_AndHookThrows_ExceptionIsSuppressed()
    {
        var (proxy, svc) = Build();

        // Hook failure is suppressed (suppressExceptions: true); original NoRollbackFor exception propagates.
        var ex = Assert.Throws<InvalidOperationException>(() => proxy.RunSyncWithNoRollbackForAndFailingHook());

        Assert.Equal("no-rollback exception", ex.Message);
        Assert.Contains("after-commit", svc.Fired); // transaction still committed
    }

    // Observer notification when sync BeforeCommit hook fails (SyncHandler line 48)

    [Fact]
    public void BeforeCommit_SyncPath_WhenHookThrows_ObserverReceivesRollback()
    {
        var (proxy, _, observer) = BuildWithObserver();

        Assert.Throws<AggregateException>(() => proxy.RunSyncWithFailingBeforeCommitHook());

        Assert.Contains("ROLLBACK:RunSyncWithFailingBeforeCommitHook", observer.Calls);
        Assert.DoesNotContain("COMMIT:RunSyncWithFailingBeforeCommitHook", observer.Calls);
    }
}
