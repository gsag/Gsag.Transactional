using System.Threading;
using Gsag.Transactional.Core.Attributes;
using Gsag.Transactional.Core.Observability;
using Gsag.Transactional.Core.Proxy;
using Xunit;

namespace Gsag.Transactional.Tests.Unit;

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
}

public class BasicService : IBasicService
{
    public string NoAttribute() => "no-tx";
    public string SyncReturn() => "ok";
    public Task<string> AsyncReturn() => Task.FromResult("ok");
    public ValueTask<string> ValueTaskGenericReturn() => ValueTask.FromResult("ok");
    public Task ThrowAsync() => Task.FromException(new InvalidOperationException("boom"));
    public void ThrowSync() => throw new InvalidOperationException("boom-sync");
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
    public string Method() => "concrete";
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

public class ProxyMechanicsTests
{
    private readonly IBasicService _proxy;

    public ProxyMechanicsTests() =>
        _proxy = TransactionProxyFactory.Create<IBasicService>(new BasicService());

    [Fact]
    public void ProxyFactory_ReturnsImplementationOfInterface()
        => Assert.IsAssignableFrom<IBasicService>(_proxy);

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
}
