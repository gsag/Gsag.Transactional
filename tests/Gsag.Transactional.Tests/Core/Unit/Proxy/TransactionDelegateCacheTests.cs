using System.Reflection;
using System.Transactions;
using Gsag.Transactional.Core.Attributes;
using Gsag.Transactional.Core.Hooks;
using Gsag.Transactional.Core.Proxy;
using Xunit;

namespace Gsag.Transactional.Tests.Core.Unit.Proxy;

public interface IDelegateCacheService
{
    [Transactional]
    Task<string> ReturnTaskAsync();

    [Transactional]
    ValueTask<string> ReturnValueTaskAsync();
}

public class DelegateCacheService : IDelegateCacheService
{
    public Task<string> ReturnTaskAsync() => Task.FromResult("task-ok");

    public ValueTask<string> ReturnValueTaskAsync() => ValueTask.FromResult("vt-ok");
}

public class TransactionDelegateCacheTests
{
    [Fact]
    public async Task CallGenericTaskWrapper_ReturnsCommittedTaskAndRecordsObserverEvents()
    {
        using var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);
        var method = typeof(IDelegateCacheService).GetMethod(nameof(IDelegateCacheService.ReturnTaskAsync))!;
        var attr = method.GetCustomAttribute<TransactionalAttribute>()!;
        var observer = new RecordingObserver();
        var hooks = new HookCollection { Role = HookCollectionRole.Owning };
        var ctx = new TransactionContext(method, scope, attr, observer, hooks);

        await TransactionDelegateCache.CallGenericTaskWrapper(typeof(string), Task.FromResult("task-ok"), ctx);
        Assert.Contains("COMMIT:ReturnTaskAsync", observer.Calls);
        Assert.Contains("COMPLETE:ReturnTaskAsync:True", observer.Calls);
    }

    [Fact]
    public async Task CallGenericValueTaskWrapper_ReturnsCommittedValueTaskAndRecordsObserverEvents()
    {
        using var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);
        var method = typeof(IDelegateCacheService).GetMethod(nameof(IDelegateCacheService.ReturnValueTaskAsync))!;
        var attr = method.GetCustomAttribute<TransactionalAttribute>()!;
        var observer = new RecordingObserver();
        var hooks = new HookCollection { Role = HookCollectionRole.Owning };
        var ctx = new TransactionContext(method, scope, attr, observer, hooks);

#pragma warning disable CA2012 // ValueTask is directly awaited inline; cast from object return type is unavoidable
        var result = await (ValueTask<string>)TransactionDelegateCache.CallGenericValueTaskWrapper(typeof(string), ValueTask.FromResult("vt-ok"), ctx);
#pragma warning restore CA2012

        Assert.Equal("vt-ok", result);
        Assert.Contains("COMMIT:ReturnValueTaskAsync", observer.Calls);
        Assert.Contains("COMPLETE:ReturnValueTaskAsync:True", observer.Calls);
    }

    [Fact]
    public async Task CreateFaultedTask_ReturnsFaultedGenericTaskWithOriginalException()
    {
        var ex = new InvalidOperationException("boom-task");
        var task = TransactionDelegateCache.CreateFaultedTask(typeof(string), ex);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () => await ((Task<string>)task));

        Assert.Equal("boom-task", thrown.Message);
    }

    [Fact]
    public async Task CreateFaultedValueTask_ReturnsFaultedGenericValueTaskWithOriginalException()
    {
        var ex = new InvalidOperationException("boom-vt");
        var boxed = TransactionDelegateCache.CreateFaultedValueTask(typeof(string), ex);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () => await ((ValueTask<string>)boxed));

        Assert.Equal("boom-vt", thrown.Message);
    }
}
