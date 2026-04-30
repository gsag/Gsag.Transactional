using System.IO;
using System.Reflection;
using System.Threading;
using System.Transactions;
using Transactional.Core.Attributes;
using Transactional.Core.Observability;
using Transactional.Core.Proxy;
using Xunit;

namespace Transactional.Tests.Unit;

// ---------------------------------------------------------------------------
// Test doubles
// ---------------------------------------------------------------------------

public interface ITestService
{
    string NotTransactional();

    [Transactional]
    string WithTransaction();

    [Transactional]
    Task<string> WithTransactionAsync();

    [Transactional]
    ValueTask<string> WithTransactionValueTaskAsync();

    [Transactional]
    Task ThrowsAsync();

    [Transactional]
    void Throws();

    [Transactional(NoRollbackFor = [typeof(OperationCanceledException)])]
    Task NoRollbackForCancelledAsync();

    [Transactional(RollbackFor = [typeof(InvalidOperationException)])]
    Task RollbackOnlyForSpecificAsync(bool throwSpecific);

    [Transactional(Propagation = TransactionScopeOption.RequiresNew)]
    string RequiresNewTransaction();

    [Transactional]
    void VoidSuccess();

    [Transactional]
    ValueTask VoidValueTaskAsync();

    [Transactional]
    ValueTask ThrowsVoidValueTaskAsync();

    [Transactional]
    Task ThrowsSynchronouslyAsync();

    [Transactional(NoRollbackFor = [typeof(OperationCanceledException)])]
    Task NoRollbackForSubclassAsync();

    [Transactional(
        RollbackFor   = [typeof(InvalidOperationException)],
        NoRollbackFor = [typeof(InvalidOperationException)])]
    Task ConflictingRulesAsync();

    [Transactional(RollbackFor = [typeof(IOException)])]
    Task RollbackForBaseClassAsync();

    [Transactional(Propagation = TransactionScopeOption.Suppress)]
    string SuppressTransaction();
}

public class TestService : ITestService
{
    public string NotTransactional() => "no-tx";
    public string WithTransaction() => "with-tx";
    public Task<string> WithTransactionAsync() => Task.FromResult("with-tx-async");
    public ValueTask<string> WithTransactionValueTaskAsync() => ValueTask.FromResult("with-tx-valuetask");
    public Task ThrowsAsync() => Task.FromException(new InvalidOperationException("boom"));
    public void Throws() => throw new InvalidOperationException("boom-sync");
    public Task NoRollbackForCancelledAsync() => Task.FromException(new OperationCanceledException());
    public Task RollbackOnlyForSpecificAsync(bool throwSpecific) =>
        throwSpecific
            ? Task.FromException(new InvalidOperationException("specific"))
            : Task.FromException(new ArgumentException("other"));
    public string RequiresNewTransaction() => "requires-new";
    public void VoidSuccess() { }
    public ValueTask VoidValueTaskAsync() => ValueTask.CompletedTask;
    public ValueTask ThrowsVoidValueTaskAsync() => ValueTask.FromException(new InvalidOperationException("vt-boom"));
    public Task ThrowsSynchronouslyAsync()
    {
        throw new InvalidOperationException("sync before task");
#pragma warning disable CS0162
        return Task.CompletedTask;
#pragma warning restore CS0162
    }
    public Task NoRollbackForSubclassAsync() =>
        Task.FromException(new TaskCanceledException());
    public Task ConflictingRulesAsync() =>
        Task.FromException(new InvalidOperationException("conflict"));
    public Task RollbackForBaseClassAsync() =>
        Task.FromException(new FileNotFoundException("file gone"));
    public string SuppressTransaction() =>
        Transaction.Current is null ? "suppressed" : "not-suppressed";
}

public class RecordingObserver : ITransactionLifecycleObserver
{
    public List<string> Calls { get; } = [];

    public void OnBegin(MethodInfo method, TransactionalAttribute attr) =>
        Calls.Add($"BEGIN:{method.Name}");

    public void OnCommit(MethodInfo method, TimeSpan elapsed) =>
        Calls.Add($"COMMIT:{method.Name}");

    public void OnRollback(MethodInfo method, Exception exception, TimeSpan elapsed) =>
        Calls.Add($"ROLLBACK:{method.Name}");
}

public class ConcurrentRecordingObserver : ITransactionLifecycleObserver
{
    private int _begins;
    private int _commits;
    public int Begins => _begins;
    public int Commits => _commits;
    public void OnBegin(MethodInfo method, TransactionalAttribute attr) =>
        Interlocked.Increment(ref _begins);
    public void OnCommit(MethodInfo method, TimeSpan elapsed) =>
        Interlocked.Increment(ref _commits);
    public void OnRollback(MethodInfo method, Exception exception, TimeSpan elapsed) { }
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

public interface IOutParamService
{
    [Transactional]
    bool TryGet(out string value);
}

public class OutParamService : IOutParamService
{
    public bool TryGet(out string value) { value = "ok"; return true; }
}

public class ThrowingOnCommitObserver : ITransactionLifecycleObserver
{
    public void OnBegin(MethodInfo method, TransactionalAttribute attr) { }
    public void OnCommit(MethodInfo method, TimeSpan elapsed) =>
        throw new InvalidOperationException("observer-commit-fail");
    public void OnRollback(MethodInfo method, Exception exception, TimeSpan elapsed) { }
}

public class ConcreteService
{
    public string Method() => "concrete";
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

public class TransactionProxyTests
{
    private readonly ITestService _proxy;
    private readonly RecordingObserver _observer;

    public TransactionProxyTests()
    {
        _observer = new RecordingObserver();
        _proxy = TransactionProxyFactory.Create<ITestService>(new TestService(), _observer);
    }

    // --- Basic proxy mechanics ---

    [Fact]
    public void ProxyFactory_ReturnsImplementationOfInterface()
        => Assert.IsAssignableFrom<ITestService>(_proxy);

    [Fact]
    public void Method_WithoutAttribute_PassesThrough()
        => Assert.Equal("no-tx", _proxy.NotTransactional());

    [Fact]
    public void Method_WithTransactionalAttribute_ExecutesAndReturns()
        => Assert.Equal("with-tx", _proxy.WithTransaction());

    [Fact]
    public async Task AsyncMethod_WithTransactionalAttribute_ExecutesAndReturns()
        => Assert.Equal("with-tx-async", await _proxy.WithTransactionAsync());

    [Fact]
    public async Task ValueTaskAsync_WithTransactionalAttribute_ExecutesAndReturns()
        => Assert.Equal("with-tx-valuetask", await _proxy.WithTransactionValueTaskAsync());

    [Fact]
    public async Task AsyncMethod_WhenThrows_PropagatesException()
        => await Assert.ThrowsAsync<InvalidOperationException>(() => _proxy.ThrowsAsync());

    [Fact]
    public void SyncMethod_WhenThrows_PropagatesException()
        => Assert.Throws<InvalidOperationException>(() => _proxy.Throws());

    // --- Observer ---

    [Fact]
    public void Observer_OnSuccessfulSync_ReceivesBeginAndCommit()
    {
        _proxy.WithTransaction();
        Assert.Contains("BEGIN:WithTransaction", _observer.Calls);
        Assert.Contains("COMMIT:WithTransaction", _observer.Calls);
    }

    [Fact]
    public async Task Observer_OnSuccessfulAsync_ReceivesBeginAndCommit()
    {
        await _proxy.WithTransactionAsync();
        Assert.Contains("BEGIN:WithTransactionAsync", _observer.Calls);
        Assert.Contains("COMMIT:WithTransactionAsync", _observer.Calls);
    }

    [Fact]
    public async Task Observer_OnException_ReceivesBeginAndRollback()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _proxy.ThrowsAsync());
        Assert.Contains("BEGIN:ThrowsAsync", _observer.Calls);
        Assert.Contains("ROLLBACK:ThrowsAsync", _observer.Calls);
    }

    [Fact]
    public void Observer_WithoutAttribute_ReceivesNoEvents()
    {
        _proxy.NotTransactional();
        Assert.Empty(_observer.Calls);
    }

    // --- NoRollbackFor ---

    [Fact]
    public async Task NoRollbackFor_WhenMatchingException_CommitsAndPropagates()
    {
        await Assert.ThrowsAsync<OperationCanceledException>(() => _proxy.NoRollbackForCancelledAsync());
        Assert.Contains("COMMIT:NoRollbackForCancelledAsync", _observer.Calls);
        Assert.DoesNotContain("ROLLBACK:NoRollbackForCancelledAsync", _observer.Calls);
    }

    // --- RollbackFor ---

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

    // --- Propagation ---

    [Fact]
    public void RequiresNew_ExecutesWithIndependentTransaction()
        => Assert.Equal("requires-new", _proxy.RequiresNewTransaction());

    // --- 7a. Attribute on interface only ---

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

    // --- 7b. Sync void successful path ---

    [Fact]
    public void SyncVoidMethod_WithTransactionalAttribute_CommitsAndObserves()
    {
        _proxy.VoidSuccess();
        Assert.Contains("BEGIN:VoidSuccess", _observer.Calls);
        Assert.Contains("COMMIT:VoidSuccess", _observer.Calls);
    }

    // --- 7c. Non-generic ValueTask — success and failure paths ---

    [Fact]
    public async Task VoidValueTask_Success_CommitsAndObserves()
    {
        await _proxy.VoidValueTaskAsync();
        Assert.Contains("BEGIN:VoidValueTaskAsync", _observer.Calls);
        Assert.Contains("COMMIT:VoidValueTaskAsync", _observer.Calls);
    }

    [Fact]
    public async Task VoidValueTask_WhenThrows_RollsBackAndObserves()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _proxy.ThrowsVoidValueTaskAsync().AsTask());
        Assert.Contains("BEGIN:ThrowsVoidValueTaskAsync", _observer.Calls);
        Assert.Contains("ROLLBACK:ThrowsVoidValueTaskAsync", _observer.Calls);
        Assert.DoesNotContain("COMMIT:ThrowsVoidValueTaskAsync", _observer.Calls);
    }

    // --- 7d. Throw synchronously before returning a Task ---

    [Fact]
    public async Task AsyncMethod_WhenThrowsSynchronouslyBeforeTask_RollsBackAndObserves()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _proxy.ThrowsSynchronouslyAsync());
        Assert.Contains("BEGIN:ThrowsSynchronouslyAsync", _observer.Calls);
        Assert.Contains("ROLLBACK:ThrowsSynchronouslyAsync", _observer.Calls);
        Assert.DoesNotContain("COMMIT:ThrowsSynchronouslyAsync", _observer.Calls);
    }

    // --- 7e. Observer on sync throw path ---

    [Fact]
    public void Observer_SyncThrow_ReceivesBeginAndRollback()
    {
        Assert.Throws<InvalidOperationException>(() => _proxy.Throws());
        Assert.Contains("BEGIN:Throws", _observer.Calls);
        Assert.Contains("ROLLBACK:Throws", _observer.Calls);
        Assert.DoesNotContain("COMMIT:Throws", _observer.Calls);
    }

    // --- 7f. NoRollbackFor with a subclass exception ---

    [Fact]
    public async Task NoRollbackFor_WhenSubclassThrown_CommitsAndPropagates()
    {
        await Assert.ThrowsAsync<TaskCanceledException>(() => _proxy.NoRollbackForSubclassAsync());
        Assert.Contains("COMMIT:NoRollbackForSubclassAsync", _observer.Calls);
        Assert.DoesNotContain("ROLLBACK:NoRollbackForSubclassAsync", _observer.Calls);
    }

    // --- 7g. NoRollbackFor and RollbackFor with the same type — NoRollbackFor wins ---

    [Fact]
    public async Task ShouldRollback_WhenTypeInBothLists_NoRollbackForWins()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _proxy.ConflictingRulesAsync());
        Assert.Contains("COMMIT:ConflictingRulesAsync", _observer.Calls);
        Assert.DoesNotContain("ROLLBACK:ConflictingRulesAsync", _observer.Calls);
    }

    // --- 7h. RollbackFor with a subclass exception ---

    [Fact]
    public async Task RollbackFor_WhenSubclassThrown_RollsBack()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => _proxy.RollbackForBaseClassAsync());
        Assert.Contains("ROLLBACK:RollbackForBaseClassAsync", _observer.Calls);
        Assert.DoesNotContain("COMMIT:RollbackForBaseClassAsync", _observer.Calls);
    }

    // --- 7i. Propagation = Suppress ---

    [Fact]
    public void Suppress_WhenCalledInsideAmbientScope_TransactionCurrentIsNull()
    {
        using var outer = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);
        var result = _proxy.SuppressTransaction();
        Assert.Equal("suppressed", result);
        outer.Complete();
    }

    // --- 7j. RequiresNew inside an ambient transaction suspends and restores it ---

    [Fact]
    public void RequiresNew_InsideAmbientScope_SuspendsAndRestoresOuterTransaction()
    {
        using var outer = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);
        var outerTx = Transaction.Current;

        _proxy.RequiresNewTransaction();

        // After the proxy call the outer ambient transaction must be restored.
        Assert.Same(outerTx, Transaction.Current);
    }

    // --- 7k. Concurrent invocations don't corrupt caches ---

    [Fact]
    public async Task ConcurrentInvocations_DoNotCorruptCacheOrState()
    {
        const int degree = 50;
        var observer = new ConcurrentRecordingObserver();
        var proxy = TransactionProxyFactory.Create<ITestService>(new TestService(), observer);

        var tasks = Enumerable.Range(0, degree)
            .Select(_ => Task.Run(() => proxy.WithTransactionAsync()));
        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.Equal("with-tx-async", r));
        Assert.Equal(degree, observer.Begins);
        Assert.Equal(degree, observer.Commits);
    }

    // --- Guard rails ---

    [Fact]
    public void Wrap_WithNullTarget_ThrowsArgumentNullException()
        => Assert.Throws<ArgumentNullException>(
            () => TransactionProxyFactory.Create<ITestService>(null!));

    [Fact]
    public void Wrap_WithConcreteClassAsT_ThrowsInvalidOperationException()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => TransactionProxy<ConcreteService>.Wrap(new ConcreteService()));
        Assert.Contains("interface", ex.Message);
        Assert.Contains(nameof(ConcreteService), ex.Message);
    }

    [Fact]
    public void Proxy_MethodWithOutParameter_ThrowsNotSupportedException()
    {
        var proxy = TransactionProxyFactory.Create<IOutParamService>(new OutParamService());
        string? value;
        var ex = Assert.Throws<NotSupportedException>(() => proxy.TryGet(out value));
        Assert.Contains("out", ex.Message.ToLowerInvariant());
    }

    [Fact]
    public void Observer_WhenOnCommitThrows_PropagatesObserverExceptionNotDoubleComplete()
    {
        var proxy = TransactionProxyFactory.Create<ITestService>(
            new TestService(), new ThrowingOnCommitObserver());

        var ex = Assert.Throws<InvalidOperationException>(() => proxy.WithTransaction());

        // If the double-Complete bug were present, the message would be about transaction state.
        Assert.Equal("observer-commit-fail", ex.Message);
    }
}
