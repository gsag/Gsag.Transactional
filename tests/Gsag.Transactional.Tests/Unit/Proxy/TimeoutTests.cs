using System.Transactions;
using Gsag.Transactional.Core.Attributes;
using Gsag.Transactional.Core.Proxy;
using Xunit;
using Gsag.Transactional.Tests.Unit;

namespace Gsag.Transactional.Tests.Unit.Proxy;

public interface ITimedService
{
    [Transactional(TimeoutSeconds = 1)]
    Task FastAsync();

    [Transactional(TimeoutSeconds = 1)]
    Task SlowAsync();
}

public class TimedService : ITimedService
{
    public Task FastAsync() => Task.CompletedTask;
    public async Task SlowAsync() => await Task.Delay(1_500);
}

public class TimeoutTests
{
    private readonly ITimedService _proxy;
    private readonly RecordingObserver _observer;

    public TimeoutTests()
    {
        _observer = new RecordingObserver();
        _proxy = TransactionProxyFactory.Create<ITimedService>(new TimedService(), _observer);
    }

    [Fact]
    public async Task Timeout_WhenMethodCompletesWithinLimit_Commits()
    {
        await _proxy.FastAsync();

        Assert.Contains("COMMIT:FastAsync", _observer.Calls);
        Assert.DoesNotContain("ROLLBACK:FastAsync", _observer.Calls);
    }

    [Fact]
    public async Task Timeout_WhenMethodExceedsLimit_ThrowsTransactionAbortedException()
    {
        await Assert.ThrowsAsync<TransactionAbortedException>(() => _proxy.SlowAsync());
    }
}
