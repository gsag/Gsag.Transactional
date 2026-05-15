using System.IO;
using Gsag.Transactional.Core.Attributes;
using Gsag.Transactional.Core.Proxy;
using Gsag.Transactional.Tests.Unit;
using Xunit;

namespace Gsag.Transactional.Tests.Unit.Proxy;

public interface IRollbackService
{
    [Transactional(NoRollbackFor = [typeof(OperationCanceledException)])]
    Task NoRollbackForCancelledAsync();

    [Transactional(RollbackFor = [typeof(InvalidOperationException)])]
    Task RollbackOnlyForSpecificAsync(bool throwSpecific);

    [Transactional(NoRollbackFor = [typeof(OperationCanceledException)])]
    Task NoRollbackForSubclassAsync();

    [Transactional(
        RollbackFor = [typeof(InvalidOperationException)],
        NoRollbackFor = [typeof(InvalidOperationException)])]
    Task ConflictingRulesAsync();

    [Transactional(RollbackFor = [typeof(IOException)])]
    Task RollbackForBaseClassAsync();

    // Return-type variants for NoRollbackFor — each exercises a separate async wrapper branch.
    [Transactional(NoRollbackFor = [typeof(OperationCanceledException)])]
    Task<string> NoRollbackForTaskGenericAsync();

    [Transactional(NoRollbackFor = [typeof(OperationCanceledException)])]
    ValueTask NoRollbackForValueTaskAsync();

    [Transactional(NoRollbackFor = [typeof(OperationCanceledException)])]
    ValueTask<string> NoRollbackForValueTaskGenericAsync();
}

public class RollbackService : IRollbackService
{
    public Task NoRollbackForCancelledAsync() =>
        Task.FromException(new OperationCanceledException());

    public Task RollbackOnlyForSpecificAsync(bool throwSpecific) =>
        throwSpecific
            ? Task.FromException(new InvalidOperationException("specific"))
            : Task.FromException(new ArgumentException("other"));

    public Task NoRollbackForSubclassAsync() =>
        Task.FromException(new TaskCanceledException());

    public Task ConflictingRulesAsync() =>
        Task.FromException(new InvalidOperationException("conflict"));

    public Task RollbackForBaseClassAsync() =>
        Task.FromException(new FileNotFoundException("file gone"));

    public Task<string> NoRollbackForTaskGenericAsync() =>
        Task.FromException<string>(new OperationCanceledException());

    public ValueTask NoRollbackForValueTaskAsync() =>
        ValueTask.FromException(new OperationCanceledException());

    public ValueTask<string> NoRollbackForValueTaskGenericAsync() =>
        ValueTask.FromException<string>(new OperationCanceledException());
}

public class RollbackRulesTests
{
    private readonly IRollbackService _proxy;
    private readonly RecordingObserver _observer;

    public RollbackRulesTests()
    {
        _observer = new RecordingObserver();
        _proxy = TransactionProxyFactory.Create<IRollbackService>(new RollbackService(), _observer);
    }

    [Fact]
    public async Task NoRollbackFor_WhenMatchingException_CommitsAndPropagates()
    {
        await Assert.ThrowsAsync<OperationCanceledException>(() => _proxy.NoRollbackForCancelledAsync());
        Assert.Contains("COMMIT:NoRollbackForCancelledAsync", _observer.Calls);
        Assert.DoesNotContain("ROLLBACK:NoRollbackForCancelledAsync", _observer.Calls);
    }

    [Fact]
    public async Task RollbackFor_OnMatchingException_RollsBack()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _proxy.RollbackOnlyForSpecificAsync(true));
        Assert.Contains("ROLLBACK:RollbackOnlyForSpecificAsync", _observer.Calls);
    }

    [Fact]
    public async Task RollbackFor_OnNonMatchingException_CommitsAndPropagates()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _proxy.RollbackOnlyForSpecificAsync(false));
        Assert.Contains("COMMIT:RollbackOnlyForSpecificAsync", _observer.Calls);
        Assert.DoesNotContain("ROLLBACK:RollbackOnlyForSpecificAsync", _observer.Calls);
    }

    [Fact]
    public async Task NoRollbackFor_WhenSubclassThrown_CommitsAndPropagates()
    {
        await Assert.ThrowsAsync<TaskCanceledException>(() => _proxy.NoRollbackForSubclassAsync());
        Assert.Contains("COMMIT:NoRollbackForSubclassAsync", _observer.Calls);
        Assert.DoesNotContain("ROLLBACK:NoRollbackForSubclassAsync", _observer.Calls);
    }

    [Fact]
    public async Task ShouldRollback_WhenTypeInBothLists_NoRollbackForWins()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _proxy.ConflictingRulesAsync());
        Assert.Contains("COMMIT:ConflictingRulesAsync", _observer.Calls);
        Assert.DoesNotContain("ROLLBACK:ConflictingRulesAsync", _observer.Calls);
    }

    [Fact]
    public async Task RollbackFor_WhenSubclassThrown_RollsBack()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => _proxy.RollbackForBaseClassAsync());
        Assert.Contains("ROLLBACK:RollbackForBaseClassAsync", _observer.Calls);
        Assert.DoesNotContain("COMMIT:RollbackForBaseClassAsync", _observer.Calls);
    }

    [Fact]
    public async Task NoRollbackFor_TaskGeneric_CommitsAndPropagates()
    {
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _proxy.NoRollbackForTaskGenericAsync());
        Assert.Contains("COMMIT:NoRollbackForTaskGenericAsync", _observer.Calls);
        Assert.DoesNotContain("ROLLBACK:NoRollbackForTaskGenericAsync", _observer.Calls);
    }

    [Fact]
    public async Task NoRollbackFor_ValueTask_CommitsAndPropagates()
    {
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _proxy.NoRollbackForValueTaskAsync().AsTask());
        Assert.Contains("COMMIT:NoRollbackForValueTaskAsync", _observer.Calls);
        Assert.DoesNotContain("ROLLBACK:NoRollbackForValueTaskAsync", _observer.Calls);
    }

    [Fact]
    public async Task NoRollbackFor_ValueTaskGeneric_CommitsAndPropagates()
    {
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _proxy.NoRollbackForValueTaskGenericAsync().AsTask());
        Assert.Contains("COMMIT:NoRollbackForValueTaskGenericAsync", _observer.Calls);
        Assert.DoesNotContain("ROLLBACK:NoRollbackForValueTaskGenericAsync", _observer.Calls);
    }

    [Fact]
    public async Task NoRollbackFor_WhenMatchingException_OnComplete_CommittedIsTrue()
    {
        await Assert.ThrowsAsync<OperationCanceledException>(() => _proxy.NoRollbackForCancelledAsync());
        Assert.Contains("COMPLETE:NoRollbackForCancelledAsync:True", _observer.Calls);
    }

    [Fact]
    public async Task NoRollbackFor_TaskGeneric_OnComplete_CommittedIsTrue()
    {
        await Assert.ThrowsAsync<OperationCanceledException>(() => _proxy.NoRollbackForTaskGenericAsync());
        Assert.Contains("COMPLETE:NoRollbackForTaskGenericAsync:True", _observer.Calls);
    }

    [Fact]
    public async Task NoRollbackFor_ValueTask_OnComplete_CommittedIsTrue()
    {
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _proxy.NoRollbackForValueTaskAsync().AsTask());
        Assert.Contains("COMPLETE:NoRollbackForValueTaskAsync:True", _observer.Calls);
    }

    [Fact]
    public async Task NoRollbackFor_ValueTaskGeneric_OnComplete_CommittedIsTrue()
    {
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _proxy.NoRollbackForValueTaskGenericAsync().AsTask());
        Assert.Contains("COMPLETE:NoRollbackForValueTaskGenericAsync:True", _observer.Calls);
    }
}
