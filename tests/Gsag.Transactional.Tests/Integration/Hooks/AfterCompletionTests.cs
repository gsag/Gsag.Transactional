using Gsag.Transactional.Core.Attributes;
using Gsag.Transactional.Core.Hooks;
using Gsag.Transactional.Core.Proxy;
using Xunit;

namespace Gsag.Transactional.Tests.Integration.Hooks;

public interface IAfterCompletionService
{
    Task CommitAsync();
    Task RollbackAsync();
    Task CommitWithFailingCompletionHookAsync();
}

public class AfterCompletionService : IAfterCompletionService
{
    private readonly ITransactionHooks _hooks;
    public List<string> Fired { get; } = [];
    public AfterCompletionService(ITransactionHooks hooks) => _hooks = hooks;

    [Transactional]
    public async Task CommitAsync()
    {
        await Task.CompletedTask;
        _hooks.AfterCommit(() => Fired.Add("after-commit"));
        _hooks.AfterRollback(() => Fired.Add("after-rollback"));
        _hooks.AfterCompletion(() => Fired.Add("after-completion"));
    }

    [Transactional]
    public async Task RollbackAsync()
    {
        _hooks.AfterCommit(() => Fired.Add("after-commit"));
        _hooks.AfterRollback(() => Fired.Add("after-rollback"));
        _hooks.AfterCompletion(() => Fired.Add("after-completion"));
        await Task.CompletedTask;
        throw new InvalidOperationException("forced rollback");
    }

    [Transactional]
    public async Task CommitWithFailingCompletionHookAsync()
    {
        await Task.CompletedTask;
        _hooks.AfterCompletion((Action)(() => throw new InvalidOperationException("completion-hook failed")));
    }
}

public class AfterCompletionTests
{
    private static (IAfterCompletionService proxy, AfterCompletionService svc) Build()
    {
        var hooks = new TransactionHooks();
        var svc   = new AfterCompletionService(hooks);
        var proxy = TransactionProxyFactory.Create<IAfterCompletionService>(svc, observer: null);
        return (proxy, svc);
    }

    [Fact]
    public async Task AfterCompletion_OnCommit_Fires()
    {
        var (proxy, svc) = Build();

        await proxy.CommitAsync();

        Assert.Contains("after-completion", svc.Fired);
    }

    [Fact]
    public async Task AfterCompletion_OnRollback_Fires()
    {
        var (proxy, svc) = Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() => proxy.RollbackAsync());

        Assert.Contains("after-completion", svc.Fired);
    }

    [Fact]
    public async Task OnCommit_OnlyAfterCommitAndAfterCompletionFire()
    {
        var (proxy, svc) = Build();

        await proxy.CommitAsync();

        Assert.Equal(["after-commit", "after-completion"], svc.Fired);
    }

    [Fact]
    public async Task OnRollback_OnlyAfterRollbackAndAfterCompletionFire()
    {
        var (proxy, svc) = Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() => proxy.RollbackAsync());

        Assert.Equal(["after-rollback", "after-completion"], svc.Fired);
    }

    /// <summary>
    /// A failing AfterCompletion hook on the commit path must throw AggregateException —
    /// there is no active exception to suppress, so hook failures propagate to the caller.
    /// </summary>
    [Fact]
    public async Task AfterCompletion_WhenHookFailsOnCommitPath_ThrowsAggregateException()
    {
        var (proxy, svc) = Build();

        var ex = await Assert.ThrowsAsync<AggregateException>(() => proxy.CommitWithFailingCompletionHookAsync());

        Assert.Single(ex.InnerExceptions);
        Assert.Equal("completion-hook failed", ex.InnerExceptions[0].Message);
    }

    /// <summary>
    /// Order guarantee: AfterCommit hooks must execute before AfterCompletion hooks.
    /// </summary>
    [Fact]
    public async Task AfterCommit_ExecutesBeforeAfterCompletion_OnCommitPath()
    {
        var (proxy, svc) = Build();

        await proxy.CommitAsync();

        var commitIdx     = svc.Fired.IndexOf("after-commit");
        var completionIdx = svc.Fired.IndexOf("after-completion");
        Assert.True(commitIdx < completionIdx, "AfterCommit must fire before AfterCompletion");
    }
}
