using System.Reflection;
using Transactional.Core.Attributes;

namespace Transactional.Core.Observability;

/// <summary>
/// Receives notifications at each stage of a transaction managed by TransactionProxy.
/// Register an implementation in DI (or pass directly to TransactionProxyFactory.Create)
/// to observe transaction lifecycle events for logging, metrics, or testing.
/// </summary>
public interface ITransactionLifecycleObserver
{
    /// <summary>Called immediately after the TransactionScope is opened.</summary>
    void OnBegin(MethodInfo method, TransactionalAttribute attr);

    /// <summary>Called when scope.Complete() is invoked (transaction will commit).</summary>
    void OnCommit(MethodInfo method, TimeSpan elapsed);

    /// <summary>Called when the transaction is aborted due to an exception.</summary>
    void OnRollback(MethodInfo method, Exception exception, TimeSpan elapsed);
}
