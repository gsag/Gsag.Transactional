using Gsag.Transactional.Core.Attributes;
using Gsag.Transactional.Core.Observability;
using Gsag.Transactional.Core.Proxy;
using Xunit;
using Gsag.Transactional.Tests.Unit;

namespace Gsag.Transactional.Tests.Unit.Observability;

public interface IObserverService
{
    [Transactional]
    string CommitSync();

    [Transactional]
    Task CommitAsync();

    [Transactional]
    Task ThrowAsync();

    string NoAttribute();

    [Transactional]
    void CommitVoidSync();

    [Transactional]
    ValueTask CommitVoidValueTask();

    [Transactional]
    ValueTask ThrowVoidValueTask();

    [Transactional]
    Task ThrowSynchronouslyAsync();

    [Transactional]
    void ThrowSync();
}

public class ObserverService : IObserverService
{
    public string CommitSync() => "ok";
    public Task CommitAsync() => Task.CompletedTask;
    public Task ThrowAsync() => Task.FromException(new InvalidOperationException("boom"));
    public string NoAttribute() => "ok";
    public void CommitVoidSync() { }
    public ValueTask CommitVoidValueTask() => ValueTask.CompletedTask;
    public ValueTask ThrowVoidValueTask() => ValueTask.FromException(new InvalidOperationException("vt-boom"));
    public Task ThrowSynchronouslyAsync()
    {
        throw new InvalidOperationException("sync before task");
#pragma warning disable CS0162
        return Task.CompletedTask;
#pragma warning restore CS0162
    }
    public void ThrowSync() => throw new InvalidOperationException("sync-boom");
}

public interface IInterfaceAttributeService
{
    [Transactional]
    string InterfaceAnnotatedMethod();
}

public class InterfaceAttributeService : IInterfaceAttributeService
{
    // [Transactional] is intentionally NOT here — only on the interface above.
    public string InterfaceAnnotatedMethod() => "ok";
}

public class ThrowingOnCommitObserver : ITransactionObserver
{
    public void OnBegin(TransactionInfo info) { }
    public void OnCommit(TransactionInfo info, TimeSpan elapsed) =>
        throw new InvalidOperationException("observer-commit-fail");
    public void OnRollback(TransactionInfo info, Exception exception, TimeSpan elapsed) { }
    public void OnComplete(TransactionInfo info, bool committed, TimeSpan elapsed) { }
}

public class ObserverTests
{
    private readonly IObserverService _proxy;
    private readonly RecordingObserver _observer;

    public ObserverTests()
    {
        _observer = new RecordingObserver();
        _proxy = TransactionProxyFactory.Create<IObserverService>(new ObserverService(), _observer);
    }

    [Fact]
    public void Observer_OnSuccessfulSync_ReceivesBeginAndCommit()
    {
        _proxy.CommitSync();
        Assert.Contains("BEGIN:CommitSync", _observer.Calls);
        Assert.Contains("COMMIT:CommitSync", _observer.Calls);
    }

    [Fact]
    public async Task Observer_OnSuccessfulAsync_ReceivesBeginAndCommit()
    {
        await _proxy.CommitAsync();
        Assert.Contains("BEGIN:CommitAsync", _observer.Calls);
        Assert.Contains("COMMIT:CommitAsync", _observer.Calls);
    }

    [Fact]
    public async Task Observer_OnException_ReceivesBeginAndRollback()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _proxy.ThrowAsync());
        Assert.Contains("BEGIN:ThrowAsync", _observer.Calls);
        Assert.Contains("ROLLBACK:ThrowAsync", _observer.Calls);
    }

    [Fact]
    public void Observer_WithoutAttribute_ReceivesNoEvents()
    {
        _proxy.NoAttribute();
        Assert.Empty(_observer.Calls);
    }

    [Fact]
    public void SyncVoidMethod_CommitsAndObserves()
    {
        _proxy.CommitVoidSync();
        Assert.Contains("BEGIN:CommitVoidSync", _observer.Calls);
        Assert.Contains("COMMIT:CommitVoidSync", _observer.Calls);
    }

    [Fact]
    public async Task VoidValueTask_Success_CommitsAndObserves()
    {
        await _proxy.CommitVoidValueTask();
        Assert.Contains("BEGIN:CommitVoidValueTask", _observer.Calls);
        Assert.Contains("COMMIT:CommitVoidValueTask", _observer.Calls);
    }

    [Fact]
    public async Task VoidValueTask_WhenThrows_RollsBackAndObserves()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _proxy.ThrowVoidValueTask().AsTask());
        Assert.Contains("BEGIN:ThrowVoidValueTask", _observer.Calls);
        Assert.Contains("ROLLBACK:ThrowVoidValueTask", _observer.Calls);
        Assert.DoesNotContain("COMMIT:ThrowVoidValueTask", _observer.Calls);
    }

    [Fact]
    public async Task AsyncMethod_WhenThrowsSynchronouslyBeforeTask_RollsBackAndObserves()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _proxy.ThrowSynchronouslyAsync());
        Assert.Contains("BEGIN:ThrowSynchronouslyAsync", _observer.Calls);
        Assert.Contains("ROLLBACK:ThrowSynchronouslyAsync", _observer.Calls);
        Assert.Contains("COMPLETE:ThrowSynchronouslyAsync:False", _observer.Calls);
        Assert.DoesNotContain("COMMIT:ThrowSynchronouslyAsync", _observer.Calls);
    }

    [Fact]
    public void Observer_SyncThrow_ReceivesBeginAndRollback()
    {
        Assert.Throws<InvalidOperationException>(() => _proxy.ThrowSync());
        Assert.Contains("BEGIN:ThrowSync", _observer.Calls);
        Assert.Contains("ROLLBACK:ThrowSync", _observer.Calls);
        Assert.DoesNotContain("COMMIT:ThrowSync", _observer.Calls);
    }

    [Fact]
    public void Observer_WhenOnCommitThrows_PropagatesObserverExceptionNotDoubleComplete()
    {
        var proxy = TransactionProxyFactory.Create<IObserverService>(
            new ObserverService(), new ThrowingOnCommitObserver());

        var ex = Assert.Throws<InvalidOperationException>(() => proxy.CommitSync());

        // If the double-Complete bug were present, the message would be about transaction state.
        Assert.Equal("observer-commit-fail", ex.Message);
    }

    [Fact]
    public void Proxy_WhenAttributeOnlyOnInterface_StillCreatesTransaction()
    {
        var observer = new RecordingObserver();
        var proxy = TransactionProxyFactory.Create<IInterfaceAttributeService>(
            new InterfaceAttributeService(), observer);

        proxy.InterfaceAnnotatedMethod();

        Assert.Contains("BEGIN:InterfaceAnnotatedMethod", observer.Calls);
        Assert.Contains("COMMIT:InterfaceAnnotatedMethod", observer.Calls);
    }
}
