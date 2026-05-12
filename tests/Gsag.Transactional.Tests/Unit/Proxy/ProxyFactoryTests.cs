using Gsag.Transactional.Core.Proxy;
using Xunit;
using Gsag.Transactional.Tests.Unit;

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

public class ProxyFactoryTests
{
    // -------------------------------------------------------------------------
    // Non-generic Create(Type, object, observer?) overload
    // -------------------------------------------------------------------------

    [Fact]
    public void Create_NonGeneric_ReturnsProxiedInterface()
    {
        var proxy = TransactionProxyFactory.Create(typeof(IBasicService), new BasicService(), null);

        Assert.IsAssignableFrom<IBasicService>(proxy);
        Assert.IsNotType<BasicService>(proxy);
    }

    [Fact]
    public void Create_NonGeneric_WithObserver_RecordsLifecycleEvents()
    {
        var observer = new RecordingObserver();
        var proxy = (IBasicService)TransactionProxyFactory.Create(
            typeof(IBasicService), new BasicService(), observer);

        proxy.SyncReturn();

        Assert.Contains("BEGIN:SyncReturn", observer.Calls);
        Assert.Contains("COMMIT:SyncReturn", observer.Calls);
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
