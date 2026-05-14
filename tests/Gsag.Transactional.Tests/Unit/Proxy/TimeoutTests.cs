using System.Transactions;
using Gsag.Transactional.Core.Attributes;
using Gsag.Transactional.Core.Proxy;
using Gsag.Transactional.Tests.Unit;
using Xunit;

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

    public async Task SlowAsync()
    {
        // Poll until the transaction aborts, capped at 10 s.
        // If the proxy ignores TimeoutSeconds = 1 and falls back to TransactionManager.DefaultTimeout
        // (~60 s), the loop exits while the transaction is still Active and scope.Complete()
        // succeeds — no exception thrown — making the test fail and catching the regression.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline &&
               Transaction.Current?.TransactionInformation.Status == TransactionStatus.Active)
        {
            await Task.Delay(50);
        }
    }
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
