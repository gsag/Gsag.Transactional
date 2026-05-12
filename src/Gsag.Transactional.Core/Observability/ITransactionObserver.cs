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

    /// <summary>Called when the transaction is aborted due to an exception.</summary>
    void OnRollback(TransactionInfo info, Exception exception, TimeSpan elapsed);

    /// <summary>
    /// Called after the transaction resolves — commit or rollback — regardless of outcome.
    /// Useful for recording execution time metrics without duplicating logic in OnCommit and OnRollback.
    /// </summary>
    void OnComplete(TransactionInfo info, bool committed, TimeSpan elapsed);
}
