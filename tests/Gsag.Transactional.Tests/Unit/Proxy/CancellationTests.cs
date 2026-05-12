using Gsag.Transactional.Core.Attributes;
using Gsag.Transactional.Core.Proxy;
using Xunit;
using Gsag.Transactional.Tests.Unit;

namespace Gsag.Transactional.Tests.Unit.Proxy;

public interface ICancellationService
{
    [Transactional]
    Task ThrowCancelledAsync(CancellationToken ct);

    [Transactional(NoRollbackFor = [typeof(OperationCanceledException)])]
    Task ThrowCancelledNoRollbackAsync(CancellationToken ct);

    [Transactional]
    Task CancelMidwayAsync(CancellationToken ct);
}

public class CancellationService : ICancellationService
{
    public Task ThrowCancelledAsync(CancellationToken ct) =>
        Task.FromException(new OperationCanceledException(ct));

    public Task ThrowCancelledNoRollbackAsync(CancellationToken ct) =>
        Task.FromException(new OperationCanceledException(ct));

    public async Task CancelMidwayAsync(CancellationToken ct)
    {
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
    }
}

public class CancellationTests
{
    private readonly ICancellationService _proxy;
    private readonly RecordingObserver _observer;

    public CancellationTests()
    {
        _observer = new RecordingObserver();
        _proxy = TransactionProxyFactory.Create<ICancellationService>(new CancellationService(), _observer);
    }

    [Fact]
    public async Task Cancellation_WhenOperationCancelledByDefault_RollsBack()
    {
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAsync<OperationCanceledException>(() => _proxy.ThrowCancelledAsync(cts.Token));

        Assert.Contains("ROLLBACK:ThrowCancelledAsync", _observer.Calls);
        Assert.DoesNotContain("COMMIT:ThrowCancelledAsync", _observer.Calls);
    }

    [Fact]
    public async Task Cancellation_WhenNoRollbackForCancelled_Commits()
    {
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _proxy.ThrowCancelledNoRollbackAsync(cts.Token));

        Assert.Contains("COMMIT:ThrowCancelledNoRollbackAsync", _observer.Calls);
        Assert.DoesNotContain("ROLLBACK:ThrowCancelledNoRollbackAsync", _observer.Calls);
    }

    [Fact]
    public async Task Cancellation_WhenTokenCancelledMidExecution_RollsBack()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => _proxy.CancelMidwayAsync(cts.Token));

        Assert.Contains("ROLLBACK:CancelMidwayAsync", _observer.Calls);
        Assert.DoesNotContain("COMMIT:CancelMidwayAsync", _observer.Calls);
    }
}
