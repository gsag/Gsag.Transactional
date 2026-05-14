namespace Gsag.Transactional.Core.Observability;

/// <summary>
/// Receives notifications at each stage of a transaction managed by TransactionProxy.
/// Register an implementation in DI (or pass directly to TransactionProxyFactory.Create)
/// to observe transaction events for logging, metrics, or testing.
/// </summary>
public interface ITransactionObserver
{
    /// <summary>Called immediately after the TransactionScope is opened.</summary>
    void OnBegin(TransactionInfo info);

    /// <summary>Called when scope.Complete() is invoked (transaction will commit).</summary>
    void OnCommit(TransactionInfo info, TimeSpan elapsed);

    /// <summary>
    /// Called when the transaction is aborted due to an exception.
    /// Not called when a <c>BeforeRollback</c> hook throws and its exception is suppressed —
    /// the rollback still happens, but <see cref="OnRollback"/> is already in progress at that point.
    /// </summary>
    void OnRollback(TransactionInfo info, Exception exception, TimeSpan elapsed);

    /// <summary>
    /// Called after the transaction resolves — commit or rollback — regardless of outcome.
    /// Useful for recording execution time metrics without duplicating logic in OnCommit and OnRollback.
    /// Any exception thrown by this method propagates to the caller of the transactional method.
    /// </summary>
    /// <example>
    /// <code>
    /// public class MetricsObserver : ITransactionObserver
    /// {
    ///     public void OnBegin(TransactionInfo info) { }
    ///     public void OnCommit(TransactionInfo info, TimeSpan elapsed) { }
    ///     public void OnRollback(TransactionInfo info, Exception ex, TimeSpan elapsed) { }
    ///     public void OnComplete(TransactionInfo info, bool committed, TimeSpan elapsed)
    ///         => _metrics.Record(info.MethodName, committed, elapsed);
    /// }
    /// </code>
    /// </example>
    void OnComplete(TransactionInfo info, bool committed, TimeSpan elapsed);
}
