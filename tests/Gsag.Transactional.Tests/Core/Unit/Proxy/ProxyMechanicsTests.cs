using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using System.Transactions;
using Gsag.Transactional.Core.Attributes;
using Gsag.Transactional.Core.Hooks;
using Gsag.Transactional.Core.Observability;
using Gsag.Transactional.Core.Proxy;
using Gsag.Transactional.Tests.Core.Unit;
using Xunit;

namespace Gsag.Transactional.Tests.Core.Unit.Proxy;

public interface IBasicService
{
    string NoAttribute();

    [Transactional]
    string SyncReturn();

    [Transactional]
    Task<string> AsyncReturn();

    [Transactional]
    ValueTask<string> ValueTaskGenericReturn();

    [Transactional]
    Task ThrowAsync();

    [Transactional]
    void ThrowSync();

    [Transactional]
    Task CommitVoidAsync();

    [Transactional]
    string Parameterless();
}

public class BasicService : IBasicService
{
    public string NoAttribute() => "no-tx";
    public string SyncReturn() => "ok";
    public Task<string> AsyncReturn() => Task.FromResult("ok");
    public ValueTask<string> ValueTaskGenericReturn() => ValueTask.FromResult("ok");
    public Task ThrowAsync() => Task.FromException(new InvalidOperationException("boom"));
    public void ThrowSync() => throw new InvalidOperationException("boom-sync");
    public Task CommitVoidAsync() => Task.CompletedTask;
    public string Parameterless() => "parameterless-ok";
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

public class ConcreteService
{
    public static string Method() => "concrete";
}

public class ConcurrentRecordingObserver : ITransactionObserver
{
    private int _begins;
    private int _commits;
    public int Begins => _begins;
    public int Commits => _commits;
    public void OnBegin(TransactionInfo info) =>
        Interlocked.Increment(ref _begins);
    public void OnCommit(TransactionInfo info, TimeSpan elapsed) =>
        Interlocked.Increment(ref _commits);
    public void OnRollback(TransactionInfo info, Exception exception, TimeSpan elapsed) { }
    public void OnComplete(TransactionInfo info, bool committed, TimeSpan elapsed) { }
}

// Service where [Transactional] is on the concrete class only (not the interface).
public interface IConcreteAttributeService
{
    string DoWork();
}

public class ConcreteAttributeService : IConcreteAttributeService
{
    [Transactional]
    public string DoWork() => "concrete-attr";
}


public interface IUnsupportedAsyncLikeService
{
    [Transactional]
    IAsyncEnumerable<int> StreamAsync();
}

public class UnsupportedAsyncLikeService : IUnsupportedAsyncLikeService
{
    private readonly ITransactionHooks _hooks;
    public List<string> Events { get; } = [];

    public UnsupportedAsyncLikeService(ITransactionHooks hooks) => _hooks = hooks;

    public async IAsyncEnumerable<int> StreamAsync()
    {
        _hooks.AfterCommit(() => Events.Add("after-commit"));
        await Task.CompletedTask;
        yield return Transaction.Current is null ? 1 : 0;
    }
}

public sealed class RecordingTraceListener : TraceListener
{
    public List<string> Messages { get; } = [];

    public override void Write(string? message) { }

    public override void WriteLine(string? message)
    {
        Messages.Add(message ?? string.Empty);
    }

    public override void TraceEvent(
        TraceEventCache? eventCache,
        string source,
        TraceEventType eventType,
        int id,
        string? message)
    {
        if (eventType == TraceEventType.Warning)
        {
            Messages.Add(message ?? string.Empty);
        }
    }
}
public class ProxyMechanicsTests
{
    private readonly IBasicService _proxy;

    public ProxyMechanicsTests() =>
        _proxy = TransactionProxyFactory.Create<IBasicService>(new BasicService());

    [Fact]
    public void ProxyFactory_ReturnsImplementationOfInterface()
        => Assert.IsType<IBasicService>(_proxy, exactMatch: false);

    [Fact]
    public void Method_WithoutAttribute_PassesThrough()
        => Assert.Equal("no-tx", _proxy.NoAttribute());

    [Fact]
    public void Method_WithTransactionalAttribute_ExecutesAndReturns()
        => Assert.Equal("ok", _proxy.SyncReturn());

    [Fact]
    public async Task AsyncMethod_WithTransactionalAttribute_ExecutesAndReturns()
        => Assert.Equal("ok", await _proxy.AsyncReturn());

    [Fact]
    public async Task ValueTaskGenericAsync_WithTransactionalAttribute_ExecutesAndReturns()
        => Assert.Equal("ok", await _proxy.ValueTaskGenericReturn());

    [Fact]
    public async Task AsyncMethod_WhenThrows_PropagatesException()
        => await Assert.ThrowsAsync<InvalidOperationException>(() => _proxy.ThrowAsync());

    [Fact]
    public void SyncMethod_WhenThrows_PropagatesException()
        => Assert.Throws<InvalidOperationException>(() => _proxy.ThrowSync());

    [Fact]
    public async Task ConcurrentInvocations_DoNotCorruptCacheOrState()
    {
        const int degree = 50;
        var observer = new ConcurrentRecordingObserver();
        var proxy = TransactionProxyFactory.Create<IBasicService>(new BasicService(), observer);

        var tasks = Enumerable.Range(0, degree)
            .Select(_ => Task.Run(() => proxy.AsyncReturn()));
        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.Equal("ok", r));
        Assert.Equal(degree, observer.Begins);
        Assert.Equal(degree, observer.Commits);
    }

    [Fact]
    public void Wrap_WithNullTarget_ThrowsArgumentNullException()
        => Assert.Throws<ArgumentNullException>(
            () => TransactionProxyFactory.Create<IBasicService>(null!));
    [Fact]
    public async Task AsyncEnumerableReturnType_SkipsTransactionAndEmitsWarning()
    {
        var listener = new RecordingTraceListener();

        lock (Trace.Listeners)
        {
            Trace.Listeners.Add(listener);
        }

        try
        {
            var hooks = new TransactionHooks();
            var svc = new UnsupportedAsyncLikeService(hooks);
            var observer = new RecordingObserver();
            var proxy = TransactionProxyFactory.Create<IUnsupportedAsyncLikeService>(svc, observer);

            var results = new List<int>();
            await foreach (var item in proxy.StreamAsync())
            {
                results.Add(item);
            }

            Assert.Equal([1], results);
            Assert.Empty(observer.Calls);
            Assert.Empty(svc.Events);
            Assert.Contains(listener.Messages, message =>
                message.Contains("skipped", StringComparison.OrdinalIgnoreCase) &&
                message.Contains("StreamAsync", StringComparison.OrdinalIgnoreCase) &&
                message.Contains("IAsyncEnumerable", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            lock (Trace.Listeners)
            {
                Trace.Listeners.Remove(listener);
            }
        }
    }

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

    // -------------------------------------------------------------------------
    // void Task — commit path fires COMMIT and COMPLETE:True
    // -------------------------------------------------------------------------

    [Fact]
    public async Task VoidTaskMethod_WhenCommits_RecordsCommitEvent()
    {
        var observer = new RecordingObserver();
        var proxy = TransactionProxyFactory.Create<IBasicService>(new BasicService(), observer);

        await proxy.CommitVoidAsync();

        Assert.Contains("COMMIT:CommitVoidAsync", observer.Calls);
    }

    [Fact]
    public async Task VoidTaskMethod_WhenCommits_RecordsCompleteWithTrue()
    {
        var observer = new RecordingObserver();
        var proxy = TransactionProxyFactory.Create<IBasicService>(new BasicService(), observer);

        await proxy.CommitVoidAsync();

        Assert.Contains("COMPLETE:CommitVoidAsync:True", observer.Calls);
    }

    [Fact]
    public async Task VoidTaskMethod_WhenCommits_CompleteFiresAfterCommit()
    {
        var observer = new RecordingObserver();
        var proxy = TransactionProxyFactory.Create<IBasicService>(new BasicService(), observer);

        await proxy.CommitVoidAsync();

        var commitIndex = observer.Calls.IndexOf("COMMIT:CommitVoidAsync");
        var completeIndex = observer.Calls.IndexOf("COMPLETE:CommitVoidAsync:True");
        Assert.True(commitIndex < completeIndex, "COMPLETE must fire after COMMIT");
    }

    // -------------------------------------------------------------------------
    // void Task — rollback path fires ROLLBACK and COMPLETE:False
    // -------------------------------------------------------------------------

    [Fact]
    public async Task VoidTaskMethod_WhenThrows_RecordsRollbackEvent()
    {
        var observer = new RecordingObserver();
        var proxy = TransactionProxyFactory.Create<IBasicService>(new BasicService(), observer);

        await Assert.ThrowsAsync<InvalidOperationException>(() => proxy.ThrowAsync());

        Assert.Contains("ROLLBACK:ThrowAsync", observer.Calls);
    }

    [Fact]
    public async Task VoidTaskMethod_WhenThrows_RecordsCompleteWithFalse()
    {
        var observer = new RecordingObserver();
        var proxy = TransactionProxyFactory.Create<IBasicService>(new BasicService(), observer);

        await Assert.ThrowsAsync<InvalidOperationException>(() => proxy.ThrowAsync());

        Assert.Contains("COMPLETE:ThrowAsync:False", observer.Calls);
    }

    [Fact]
    public async Task VoidTaskMethod_WhenThrows_DoesNotRecordCommit()
    {
        var observer = new RecordingObserver();
        var proxy = TransactionProxyFactory.Create<IBasicService>(new BasicService(), observer);

        await Assert.ThrowsAsync<InvalidOperationException>(() => proxy.ThrowAsync());

        Assert.DoesNotContain("COMMIT:ThrowAsync", observer.Calls);
    }

    // -------------------------------------------------------------------------
    // Null args — parameterless [Transactional] method invoked via the proxy
    // exercises the args ??= [] fallback in Invoke()
    // -------------------------------------------------------------------------

    [Fact]
    public void Parameterless_TransactionalMethod_ExecutesSuccessfully()
    {
        var observer = new RecordingObserver();
        var proxy = TransactionProxyFactory.Create<IBasicService>(new BasicService(), observer);

        var result = proxy.Parameterless();

        Assert.Equal("parameterless-ok", result);
    }

    [Fact]
    public void Parameterless_TransactionalMethod_RecordsCommit()
    {
        var observer = new RecordingObserver();
        var proxy = TransactionProxyFactory.Create<IBasicService>(new BasicService(), observer);

        proxy.Parameterless();

        Assert.Contains("COMMIT:Parameterless", observer.Calls);
    }

    // -------------------------------------------------------------------------
    // Attribute resolved from concrete class (not interface)
    // -------------------------------------------------------------------------

    [Fact]
    public void AttributeOnConcreteClass_NotInterface_IsPickedUpByProxy()
    {
        var observer = new RecordingObserver();
        var proxy = TransactionProxyFactory.Create<IConcreteAttributeService>(
            new ConcreteAttributeService(), observer);

        proxy.DoWork();

        Assert.Contains("BEGIN:DoWork", observer.Calls);
        Assert.Contains("COMMIT:DoWork", observer.Calls);
    }

    [Fact]
    public void AttributeOnConcreteClass_NotInterface_RecordsCompleteEvent()
    {
        var observer = new RecordingObserver();
        var proxy = TransactionProxyFactory.Create<IConcreteAttributeService>(
            new ConcreteAttributeService(), observer);

        proxy.DoWork();

        Assert.Contains("COMPLETE:DoWork:True", observer.Calls);
    }

    // -------------------------------------------------------------------------
    // FindAttribute — defensive guard: null DeclaringType
    //
    // DynamicMethod.DeclaringType is null, exercising the guard that prevents
    // GetInterfaceMap from being called with a null argument.
    // -------------------------------------------------------------------------

    [Fact]
    public void FindAttribute_WhenDeclaringTypeIsNull_ReturnsNull()
    {
        var dm = new DynamicMethod("Orphan", typeof(void), Type.EmptyTypes);
        var findAttribute = typeof(TransactionProxy<IBasicService>)
            .GetMethod("FindAttribute", BindingFlags.NonPublic | BindingFlags.Static)!;

        (MethodInfo Method, Type Concrete) key = (dm, typeof(BasicService));
        var result = findAttribute.Invoke(null, [key]);

        Assert.Null(result);
    }
}


