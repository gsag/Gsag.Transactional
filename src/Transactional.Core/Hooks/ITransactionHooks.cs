namespace Transactional.Core.Hooks;

/// <summary>
/// Schedules callbacks to run at transaction lifecycle points inside a [Transactional] scope.
/// Inject this interface into any service that needs to react to commit or rollback
/// (e.g. publishing domain events, compensating side effects, clearing caches).
///
/// Lifecycle:
///   - Register hooks from anywhere inside a [Transactional] method.
///   - The proxy runs hooks after TransactionScope.Dispose() resolves the outcome.
///   - No-op if called outside a [Transactional] scope or inside a Suppress scope.
/// </summary>
public interface ITransactionHooks
{
    /// <summary>
    /// Schedules a synchronous callback after the current transaction commits.
    /// Sync hooks always execute before async hooks — if ordering relative to an async
    /// hook matters, combine both into a single <see cref="AfterCommit(Func{Task})"/> call.
    /// </summary>
    void AfterCommit(Action action);

    /// <summary>
    /// Schedules an async callback after the current transaction commits.
    /// Not supported inside synchronous [Transactional] methods — the proxy throws
    /// <see cref="NotSupportedException"/> after the method returns.
    /// Async hooks always execute after all sync hooks, regardless of registration order.
    /// </summary>
    void AfterCommit(Func<Task> action);

    /// <summary>
    /// Schedules a synchronous callback after the current transaction rolls back.
    /// Useful for compensating side effects (e.g. deleting an uploaded file, flagging
    /// an outbox message as cancelled). Hook failures are suppressed so they do not
    /// mask the original rollback exception.
    /// </summary>
    void AfterRollback(Action action);

    /// <summary>
    /// Schedules an async callback after the current transaction rolls back.
    /// Not supported inside synchronous [Transactional] methods — the proxy throws
    /// <see cref="NotSupportedException"/> after the method returns.
    /// Hook failures are suppressed so they do not mask the original rollback exception.
    /// </summary>
    void AfterRollback(Func<Task> action);

    /// <summary>
    /// Schedules a synchronous callback that fires after the transaction resolves,
    /// regardless of whether it committed or rolled back.
    /// Useful for cleanup that must always run (e.g. releasing resources, flushing logs).
    /// Hook failures are suppressed on the rollback and NoRollbackFor paths so they
    /// do not mask an already-propagating exception.
    /// </summary>
    void AfterCompletion(Action action);

    /// <summary>
    /// Schedules an async callback that fires after the transaction resolves,
    /// regardless of whether it committed or rolled back.
    /// Not supported inside synchronous [Transactional] methods — the proxy throws
    /// <see cref="NotSupportedException"/> after the method returns.
    /// </summary>
    void AfterCompletion(Func<Task> action);
}
