namespace Gsag.Transactional.Core.Hooks;

/// <summary>
/// Schedules callbacks to run at transaction lifecycle points inside a [Transactional] scope.
/// Inject this interface into any service that needs to react to commit or rollback
/// (e.g. publishing domain events, compensating side effects, clearing caches).
///
/// Lifecycle:
///   - Register hooks from anywhere inside a [Transactional] method.
///   - Before* hooks run inside the scope — failures can still influence the transaction outcome.
///   - After* hooks run after TransactionScope.Dispose() resolves the outcome.
///   - No-op if called outside a [Transactional] scope or inside a Suppress scope.
///
/// Async overloads inside synchronous [Transactional] methods: all async overloads
/// (<see cref="BeforeCommit(Func{Task})"/>, <see cref="BeforeRollback(Func{Task})"/>,
/// <see cref="AfterCommit(Func{Task})"/>, <see cref="AfterRollback(Func{Task})"/>,
/// <see cref="AfterCompletion(Func{Task})"/>) always throw <see cref="NotSupportedException"/>
/// after the method returns, regardless of the event type and transaction outcome.
/// The proxy cannot await them on the synchronous call path. Change the method return type
/// to <see cref="System.Threading.Tasks.Task"/> or <see cref="System.Threading.Tasks.Task{T}"/>
/// to use async hooks.
/// </summary>
public interface ITransactionHooks
{
    /// <summary>
    /// Schedules a synchronous callback to run before the current transaction commits,
    /// while still inside the TransactionScope. If the hook throws, the transaction rolls back.
    /// Does not fire on the rollback path.
    /// </summary>
    void BeforeCommit(Action action);

    /// <summary>
    /// Schedules an async callback to run before the current transaction commits,
    /// while still inside the TransactionScope. If the hook throws, the transaction rolls back.
    /// Does not fire on the rollback path.
    /// On the NoRollbackFor path (a business exception is already propagating and the transaction
    /// still commits), hook failures are suppressed silently — the original exception always takes
    /// precedence and no observer notification is issued for the hook failure.
    /// Not supported inside synchronous [Transactional] methods — the proxy throws
    /// <see cref="NotSupportedException"/> after the method returns.
    /// </summary>
    void BeforeCommit(Func<Task> action);

    /// <summary>
    /// Schedules a synchronous callback to run before the current transaction rolls back,
    /// while still inside the TransactionScope (before Dispose). Hook failures are suppressed
    /// so they do not mask the original rollback exception.
    /// Does not fire on the success or NoRollbackFor paths.
    /// Note: does not fire when rollback is caused by voting via
    /// <c>Transaction.Current.Rollback()</c> without throwing an exception — in that case the
    /// method completes normally and the rollback is only detected when <c>scope.Complete()</c>
    /// throws <c>TransactionAbortedException</c>, which is handled on the BeforeCommit path.
    /// </summary>
    void BeforeRollback(Action action);

    /// <summary>
    /// Schedules an async callback to run before the current transaction rolls back,
    /// while still inside the TransactionScope (before Dispose). Hook failures are suppressed
    /// so they do not mask the original rollback exception.
    /// Does not fire on the success or NoRollbackFor paths.
    /// Note: does not fire when rollback is caused by voting via
    /// <c>Transaction.Current.Rollback()</c> without throwing an exception — in that case the
    /// method completes normally and the rollback is only detected when <c>scope.Complete()</c>
    /// throws <c>TransactionAbortedException</c>, which is handled on the BeforeCommit path.
    /// Not supported inside synchronous [Transactional] methods — the proxy throws
    /// <see cref="NotSupportedException"/> after the method returns.
    /// </summary>
    void BeforeRollback(Func<Task> action);

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
