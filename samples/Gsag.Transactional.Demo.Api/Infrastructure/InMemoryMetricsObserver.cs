using Gsag.Transactional.Core.Observability;

namespace Gsag.Transactional.Demo.Api.Infrastructure;

/// <summary>
/// Demo implementation of <see cref="ITransactionObserver"/> that accumulates
/// in-memory transaction counters. Registered alongside <see cref="LoggingTransactionObserver"/>
/// to demonstrate the Composite Observer pattern — the proxy calls both in registration order.
/// Thread-safe via Interlocked for concurrent HTTP requests.
/// </summary>
public sealed class InMemoryMetricsObserver : ITransactionObserver
{
    private long _totalTransactions;
    private long _committed;
    private long _rolledBack;
    private long _completedCount;
    private long _totalElapsedMs;

    public long TotalTransactions => Interlocked.Read(ref _totalTransactions);
    public long Committed => Interlocked.Read(ref _committed);
    public long RolledBack => Interlocked.Read(ref _rolledBack);
    public long CompletedCount => Interlocked.Read(ref _completedCount);
    public long TotalElapsedMs => Interlocked.Read(ref _totalElapsedMs);

    public void OnBegin(TransactionInfo info) =>
        Interlocked.Increment(ref _totalTransactions);

    public void OnCommit(TransactionInfo info, TimeSpan elapsed) =>
        Interlocked.Increment(ref _committed);

    public void OnRollback(TransactionInfo info, Exception exception, TimeSpan elapsed) =>
        Interlocked.Increment(ref _rolledBack);

    public void OnComplete(TransactionInfo info, bool committed, TimeSpan elapsed)
    {
        Interlocked.Increment(ref _completedCount);
        Interlocked.Add(ref _totalElapsedMs, (long)elapsed.TotalMilliseconds);
    }
}
