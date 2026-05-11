using System.Reflection;
using Transactional.Core.Attributes;
using Transactional.Core.Observability;

namespace Transactional.Demo.Api.Infrastructure;

/// <summary>
/// Demo implementation of <see cref="ITransactionLifecycleObserver"/> that accumulates
/// in-memory transaction counters. Registered alongside <see cref="LoggingTransactionObserver"/>
/// to demonstrate the Composite Observer pattern — the proxy calls both in registration order.
/// Thread-safe via Interlocked for concurrent HTTP requests.
/// </summary>
public sealed class InMemoryMetricsObserver : ITransactionLifecycleObserver
{
    private long _totalTransactions;
    private long _committed;
    private long _rolledBack;
    private long _completedCount;
    private long _totalElapsedMs;

    public long TotalTransactions => Interlocked.Read(ref _totalTransactions);
    public long Committed         => Interlocked.Read(ref _committed);
    public long RolledBack        => Interlocked.Read(ref _rolledBack);
    public long CompletedCount    => Interlocked.Read(ref _completedCount);
    public long TotalElapsedMs    => Interlocked.Read(ref _totalElapsedMs);

    public void OnBegin(MethodInfo method, TransactionalAttribute attr) =>
        Interlocked.Increment(ref _totalTransactions);

    public void OnCommit(MethodInfo method, TimeSpan elapsed) =>
        Interlocked.Increment(ref _committed);

    public void OnRollback(MethodInfo method, Exception exception, TimeSpan elapsed) =>
        Interlocked.Increment(ref _rolledBack);

    public void OnComplete(MethodInfo method, bool committed, TimeSpan elapsed)
    {
        Interlocked.Increment(ref _completedCount);
        Interlocked.Add(ref _totalElapsedMs, (long)elapsed.TotalMilliseconds);
    }
}
