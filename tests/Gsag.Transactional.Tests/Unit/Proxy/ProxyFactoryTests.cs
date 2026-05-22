using Gsag.Transactional.Core.Proxy;
using Gsag.Transactional.Tests.Unit;
using Xunit;

namespace Gsag.Transactional.Tests.Unit.Proxy;

// ValueTask services that throw before producing a ValueTask — exercises the
// synchronous-preamble catch block in HandleValueTask / HandleValueTaskGeneric.
public interface IValueTaskSyncThrowService
{
    [Gsag.Transactional.Core.Attributes.Transactional]
    System.Threading.Tasks.ValueTask RunAsync();

    [Gsag.Transactional.Core.Attributes.Transactional]
    System.Threading.Tasks.ValueTask<string> RunGenericAsync();
}

public class ValueTaskSyncThrowService : IValueTaskSyncThrowService
{
    public System.Threading.Tasks.ValueTask RunAsync() =>
        throw new InvalidOperationException("sync-vt");

    public System.Threading.Tasks.ValueTask<string> RunGenericAsync() =>
        throw new InvalidOperationException("sync-vt-generic");
}

// Second distinct interface type for multi-type cache tests.
public interface ISecondFactoryService
{
    [Gsag.Transactional.Core.Attributes.Transactional]
    string Ping();
}

public class SecondFactoryService : ISecondFactoryService
{
    public string Ping() => "pong";
}

public class ProxyFactoryTests
{
    // -------------------------------------------------------------------------
    // Non-generic Create(Type, object, observer?) overload
    // -------------------------------------------------------------------------

    [Fact]
    public void Create_NonGeneric_ReturnsProxiedInterface()
    {
        var proxy = TransactionProxyFactory.Create(typeof(IBasicService), new BasicService(), null);

        Assert.IsType<IBasicService>(proxy, exactMatch: false);
        Assert.IsNotType<BasicService>(proxy);
    }

    [Fact]
    public void Create_NonGeneric_WithObserver_RecordsLifecycleEvents()
    {
        var observer = new RecordingObserver();
        var proxy = TransactionProxyFactory.Create<IBasicService>(
            new BasicService(), observer);

        proxy.SyncReturn();

        Assert.Contains("BEGIN:SyncReturn", observer.Calls);
        Assert.Contains("COMMIT:SyncReturn", observer.Calls);
    }

    [Fact]
    public void Create_NonGeneric_CalledTwiceWithSameType_BothCallsWork()
    {
        var observer1 = new RecordingObserver();
        var observer2 = new RecordingObserver();

        var proxy1 = TransactionProxyFactory.Create<IBasicService>(
            new BasicService(), observer1);
        var proxy2 = TransactionProxyFactory.Create<IBasicService>(
            new BasicService(), observer2);

        proxy1.SyncReturn();
        proxy2.SyncReturn();

        Assert.Contains("COMMIT:SyncReturn", observer1.Calls);
        Assert.Contains("COMMIT:SyncReturn", observer2.Calls);
    }

    [Fact]
    public void Create_NonGeneric_WithDifferentInterfaceTypes_EachReturnsCorrectType()
    {
        var proxy1 = TransactionProxyFactory.Create(typeof(IBasicService), new BasicService(), null);
        var proxy2 = TransactionProxyFactory.Create(typeof(ISecondFactoryService), new SecondFactoryService(), null);

        Assert.IsType<IBasicService>(proxy1, exactMatch: false);
        Assert.IsType<ISecondFactoryService>(proxy2, exactMatch: false);
        Assert.IsNotType<BasicService>(proxy1);
        Assert.IsNotType<SecondFactoryService>(proxy2);
    }

    [Fact]
    public void Create_NonGeneric_WithDifferentInterfaceTypes_EachRoutesThroughCorrectProxy()
    {
        var obs1 = new RecordingObserver();
        var obs2 = new RecordingObserver();

        var proxy1 = TransactionProxyFactory.Create<IBasicService>(new BasicService(), obs1);
        var proxy2 = TransactionProxyFactory.Create<ISecondFactoryService>(new SecondFactoryService(), obs2);

        proxy1.SyncReturn();
        proxy2.Ping();

        Assert.Contains("COMMIT:SyncReturn", obs1.Calls);
        Assert.DoesNotContain(obs1.Calls, c => c.Contains("Ping"));
        Assert.Contains("COMMIT:Ping", obs2.Calls);
        Assert.DoesNotContain(obs2.Calls, c => c.Contains("SyncReturn"));
    }

    [Fact]
    public void Create_NonGeneric_NullObserver_DoesNotThrow()
    {
        var proxy = (IBasicService)TransactionProxyFactory.Create(typeof(IBasicService), new BasicService(), null);

        var result = proxy.SyncReturn();

        Assert.Equal("ok", result);
    }

    [Fact]
    public void Create_NonGeneric_NullObserver_RecordsNoEvents()
    {
        var recordingProxy = (IBasicService)TransactionProxyFactory.Create(
            typeof(IBasicService), new BasicService(), null);

        // Calling with null observer must not throw and must still execute the method.
        var result = recordingProxy.SyncReturn();

        Assert.Equal("ok", result);
    }

    [Fact]
    public void Create_NonGeneric_WithObserver_ObserverReceivesBeginAndComplete()
    {
        var observer = new RecordingObserver();
        var proxy = TransactionProxyFactory.Create<IBasicService>(
            new BasicService(), observer);

        proxy.SyncReturn();

        Assert.Contains("BEGIN:SyncReturn", observer.Calls);
        Assert.Contains("COMPLETE:SyncReturn:True", observer.Calls);
    }

    // -------------------------------------------------------------------------
    // ValueTask — synchronous throw before the ValueTask is produced.
    // Exercises the catch block in HandleValueTask / HandleValueTaskGeneric that
    // fires when InvokeTarget itself throws, not when the awaited task faults.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ValueTask_WhenThrowsSynchronously_RollsBackAndPropagates()
    {
        var observer = new RecordingObserver();
        var proxy = TransactionProxyFactory.Create<IValueTaskSyncThrowService>(
            new ValueTaskSyncThrowService(), observer);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => proxy.RunAsync().AsTask());

        Assert.Contains("BEGIN:RunAsync", observer.Calls);
        Assert.Contains("ROLLBACK:RunAsync", observer.Calls);
        Assert.DoesNotContain("COMMIT:RunAsync", observer.Calls);
    }

    [Fact]
    public async Task ValueTaskT_WhenThrowsSynchronously_RollsBackAndPropagates()
    {
        var observer = new RecordingObserver();
        var proxy = TransactionProxyFactory.Create<IValueTaskSyncThrowService>(
            new ValueTaskSyncThrowService(), observer);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => proxy.RunGenericAsync().AsTask());

        Assert.Contains("BEGIN:RunGenericAsync", observer.Calls);
        Assert.Contains("ROLLBACK:RunGenericAsync", observer.Calls);
        Assert.DoesNotContain("COMMIT:RunGenericAsync", observer.Calls);
    }
}
