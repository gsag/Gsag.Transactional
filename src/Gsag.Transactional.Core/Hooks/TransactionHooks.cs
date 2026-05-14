using System.Transactions;
using Gsag.Transactional.Core.Attributes;

namespace Gsag.Transactional.Core.Hooks;

/// <summary>
/// Internal implementation of <see cref="ITransactionHooks"/>.
/// Uses <see cref="System.Threading.AsyncLocal{T}"/> so each concurrent execution context
/// (i.e., each HTTP request in ASP.NET Core) gets its own isolated <see cref="HookCollection"/>.
///
/// The AsyncLocal stores a nullable reference. Null means "no active scope" — hook registrations
/// are no-ops when called outside a [Transactional] method or inside a Suppress scope.
/// </summary>
internal sealed class TransactionHooks : ITransactionHooks
{
    private static readonly AsyncLocal<HookCollection?> _current = new();

    public void AfterCommit(Action action) => _current.Value?.AddSync(HookEvent.AfterCommit, action);
    public void AfterCommit(Func<Task> action) => _current.Value?.AddAsync(HookEvent.AfterCommit, action);

    public void AfterRollback(Action action) => _current.Value?.AddSync(HookEvent.AfterRollback, action);
    public void AfterRollback(Func<Task> action) => _current.Value?.AddAsync(HookEvent.AfterRollback, action);

    public void AfterCompletion(Action action) => _current.Value?.AddSync(HookEvent.AfterCompletion, action);
    public void AfterCompletion(Func<Task> action) => _current.Value?.AddAsync(HookEvent.AfterCompletion, action);

    public void BeforeCommit(Action action) => _current.Value?.AddSync(HookEvent.BeforeCommit, action);
    public void BeforeCommit(Func<Task> action) => _current.Value?.AddAsync(HookEvent.BeforeCommit, action);

    public void BeforeRollback(Action action) => _current.Value?.AddSync(HookEvent.BeforeRollback, action);
    public void BeforeRollback(Func<Task> action) => _current.Value?.AddAsync(HookEvent.BeforeRollback, action);

    /// <summary>
    /// Called by <c>TransactionScopeExecutor</c> before opening the scope.
    /// Always returns a <see cref="HookCollection"/> — callers check <see cref="HookCollection.HasHooksFor"/>
    /// rather than null. The returned collection's <see cref="HookCollection.Previous"/> always
    /// holds the snapshot of the AsyncLocal slot before this call, so <see cref="ClearScope"/>
    /// can restore it unconditionally regardless of propagation mode.
    ///
    /// - Required joining existing ambient: returns a <see cref="HookCollectionRole.Joining"/> throwaway
    ///   without changing AsyncLocal. Hooks registered during the call flow into the ambient (parent)
    ///   collection via _current.Value. ClearScope is a no-op for joining collections.
    /// - RequiresNew / Required with no ambient: sets a new <see cref="HookCollectionRole.Owning"/>
    ///   collection as current; Previous captures the old value so ClearScope restores it.
    /// - Suppress: nulls the slot so hook registrations are no-ops inside the suppressed scope;
    ///   returns a <see cref="HookCollectionRole.SuppressThrowaway"/> with Previous set so ClearScope
    ///   restores the outer collection when the suppressed scope exits.
    /// </summary>
    internal static HookCollection BeginScope(TransactionalAttribute attr)
    {
        var previous = _current.Value;

        switch (attr.Propagation)
        {
            case TransactionScopeOption.Required when Transaction.Current is not null:
                // Joining: do not touch _current — hooks registered inside this call flow into
                // the ambient parent collection. Return a throwaway so ClearScope is a no-op.
                return new HookCollection { Role = HookCollectionRole.Joining, Previous = previous };

            case TransactionScopeOption.Required:
            case TransactionScopeOption.RequiresNew:
                var hooks = new HookCollection { Role = HookCollectionRole.Owning, Previous = previous };
                _current.Value = hooks;
                return hooks;

            case TransactionScopeOption.Suppress:
                _current.Value = null;
                return new HookCollection { Role = HookCollectionRole.SuppressThrowaway, Previous = previous };

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(attr),
                    attr.Propagation,
                    $"Unsupported TransactionScopeOption value '{attr.Propagation}'.");
        }
    }

    /// <summary>
    /// Triggers all hooks registered for <paramref name="evt"/> — sync first, then async —
    /// in registration order. All hooks run even if one throws: exceptions are collected and
    /// rethrown as an <see cref="AggregateException"/> so no side effect is silently suppressed.
    /// Pass <paramref name="suppressExceptions"/> = true on paths where an exception is already
    /// propagating so that hook failures do not mask it.
    /// </summary>
    internal static async Task TriggerAsync(HookCollection hooks, HookEvent evt, bool suppressExceptions = false)
    {
        List<Exception>? errors = null;

        foreach (var hook in hooks.SyncFor(evt))
        {
            try
            {
                hook();
            }
            catch (Exception ex)
            {
                (errors ??= []).Add(ex);
            }
        }

        foreach (var hook in hooks.AsyncFor(evt))
        {
            try
            {
                await hook().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                (errors ??= []).Add(ex);
            }
        }

        if (errors is not null && !suppressExceptions)
        {
            throw new AggregateException($"One or more {EventLabel(evt)} hooks failed.", errors);
        }
    }

    /// <summary>
    /// Triggers only the synchronous hooks registered for <paramref name="evt"/>.
    /// Mirrors <see cref="TriggerAsync"/>: all hooks run even if one throws —
    /// exceptions are collected and rethrown as an <see cref="AggregateException"/>.
    /// Pass <paramref name="suppressExceptions"/> = true on paths where an exception is already
    /// propagating so that hook failures do not mask it.
    /// Safe to call only after <see cref="EnsureNoAsyncHooks"/> has confirmed no async
    /// hooks are registered for <paramref name="evt"/>.
    /// </summary>
    internal static void TriggerSync(HookCollection hooks, HookEvent evt, bool suppressExceptions = false)
    {
        List<Exception>? errors = null;

        foreach (var hook in hooks.SyncFor(evt))
        {
            try
            {
                hook();
            }
            catch (Exception ex)
            {
                (errors ??= []).Add(ex);
            }
        }

        if (errors is not null && !suppressExceptions)
        {
            throw new AggregateException($"One or more {EventLabel(evt)} hooks failed.", errors);
        }
    }

    /// <summary>
    /// Throws <see cref="NotSupportedException"/> if any async hooks were registered
    /// for <paramref name="evt"/> inside a synchronous [Transactional] method.
    /// Must be called before <see cref="TriggerSync"/> to avoid partial side-effects
    /// before the exception.
    /// </summary>
    internal static void EnsureNoAsyncHooks(HookCollection hooks, HookEvent evt)
    {
        if (hooks.AsyncFor(evt).Count > 0)
        {
            throw new NotSupportedException(
                "Async hooks cannot be awaited on a synchronous [Transactional] call path. " +
                "Change the method return type to Task, Task<T>, ValueTask, or ValueTask<T>.");
        }
    }

    /// <summary>
    /// Fires all hooks whose event matches the given <paramref name="outcome"/>,
    /// for use on synchronous [Transactional] call paths.
    /// Exceptions are suppressed whenever an exception is already propagating
    /// (rollback or NoRollbackFor path) to avoid masking the original error.
    /// </summary>
    internal static void RunSyncHooks(HookCollection hooks, TransactionOutcome outcome)
    {
        // Pre-check all After* events before executing any hook so that no side-effect occurs
        // before a potential NotSupportedException is thrown. All events are checked
        // regardless of outcome to fail fast consistently.
        // Note: BeforeCommit and BeforeRollback are NOT checked here — they are verified and
        // executed inline at their respective call sites in HandleSync (before Commit/Rollback),
        // so by the time this finally-block runs they have already completed or been skipped.
        EnsureNoAsyncHooks(hooks, HookEvent.AfterCommit);
        EnsureNoAsyncHooks(hooks, HookEvent.AfterRollback);
        EnsureNoAsyncHooks(hooks, HookEvent.AfterCompletion);

        var suppress = outcome != TransactionOutcome.Committed;

        if (outcome != TransactionOutcome.RolledBack && hooks.HasHooksFor(HookEvent.AfterCommit))
        {
            TriggerSync(hooks, HookEvent.AfterCommit, suppressExceptions: suppress);
        }

        if (outcome == TransactionOutcome.RolledBack && hooks.HasHooksFor(HookEvent.AfterRollback))
        {
            TriggerSync(hooks, HookEvent.AfterRollback, suppressExceptions: suppress);
        }

        if (hooks.HasHooksFor(HookEvent.AfterCompletion))
        {
            TriggerSync(hooks, HookEvent.AfterCompletion, suppressExceptions: suppress);
        }
    }

    // -------------------------------------------------------------------------
    // Before* hook runners — called inline at their respective call sites,
    // not from RunSyncHooks/RunAsyncHooksAsync (which cover only After* events).
    // -------------------------------------------------------------------------

    /// <summary>
    /// Runs BeforeCommit hooks on the async path.
    /// Pass <paramref name="suppressExceptions"/> = false on the success path so that a failing
    /// hook causes a rollback; pass true on the NoRollbackFor path where an exception is already
    /// propagating and hook failures must not mask it.
    /// </summary>
    internal static Task RunBeforeCommitHooksAsync(HookCollection hooks, bool suppressExceptions = false) =>
        hooks.HasHooksFor(HookEvent.BeforeCommit)
            ? TriggerAsync(hooks, HookEvent.BeforeCommit, suppressExceptions)
            : Task.CompletedTask;

    /// <summary>
    /// Runs BeforeRollback hooks on the async path. Hook failures are always suppressed
    /// so they do not mask the original rollback exception.
    /// </summary>
    internal static Task RunBeforeRollbackHooksAsync(HookCollection hooks) =>
        hooks.HasHooksFor(HookEvent.BeforeRollback)
            ? TriggerAsync(hooks, HookEvent.BeforeRollback, suppressExceptions: true)
            : Task.CompletedTask;

    /// <summary>
    /// Runs BeforeCommit hooks on the synchronous path. Async registrations throw
    /// <see cref="NotSupportedException"/> before any hook executes.
    /// </summary>
    internal static void RunBeforeCommitSyncHooks(HookCollection hooks, bool suppressExceptions = false)
    {
        EnsureNoAsyncHooks(hooks, HookEvent.BeforeCommit);
        TriggerSync(hooks, HookEvent.BeforeCommit, suppressExceptions);
    }

    /// <summary>
    /// Runs BeforeRollback hooks on the synchronous path. Hook failures are always suppressed.
    /// Async registrations throw <see cref="NotSupportedException"/> before any hook executes.
    /// </summary>
    internal static void RunBeforeRollbackSyncHooks(HookCollection hooks)
    {
        EnsureNoAsyncHooks(hooks, HookEvent.BeforeRollback);
        TriggerSync(hooks, HookEvent.BeforeRollback, suppressExceptions: true);
    }

    private static string EventLabel(HookEvent evt) => evt switch
    {
        HookEvent.BeforeCommit => "before-commit",
        HookEvent.AfterCommit => "after-commit",
        HookEvent.BeforeRollback => "before-rollback",
        HookEvent.AfterRollback => "after-rollback",
        HookEvent.AfterCompletion => "after-completion",
        _ => evt.ToString()
    };

    /// <summary>
    /// Fires all hooks whose event matches the given <paramref name="outcome"/>,
    /// for use on async [Transactional] call paths.
    /// </summary>
    internal static async Task RunAsyncHooksAsync(HookCollection hooks, TransactionOutcome outcome)
    {
        var suppress = outcome != TransactionOutcome.Committed;

        if (outcome != TransactionOutcome.RolledBack && hooks.HasHooksFor(HookEvent.AfterCommit))
        {
            await TriggerAsync(hooks, HookEvent.AfterCommit, suppressExceptions: suppress).ConfigureAwait(false);
        }

        if (outcome == TransactionOutcome.RolledBack && hooks.HasHooksFor(HookEvent.AfterRollback))
        {
            await TriggerAsync(hooks, HookEvent.AfterRollback, suppressExceptions: suppress).ConfigureAwait(false);
        }

        if (hooks.HasHooksFor(HookEvent.AfterCompletion))
        {
            await TriggerAsync(hooks, HookEvent.AfterCompletion, suppressExceptions: suppress).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Restores the <see cref="AsyncLocal"/> slot to the value active before
    /// <paramref name="ownedHooks"/> was created. Three paths driven by <see cref="HookCollectionRole"/>:
    ///
    /// - <see cref="HookCollectionRole.Owning"/>: <c>_current.Value</c> is the collection itself;
    ///   <see cref="ReferenceEquals"/> confirms ownership and restores <see cref="HookCollection.Previous"/>.
    /// - <see cref="HookCollectionRole.SuppressThrowaway"/>: <c>_current.Value</c> was set to null
    ///   (not to the throwaway), so <see cref="ReferenceEquals"/> would never match. If the slot is
    ///   still null (no nested scope inside the suppressed region changed it), restore Previous.
    /// - <see cref="HookCollectionRole.Joining"/>: no-op — the outer scope owns the slot.
    /// </summary>
    internal static void ClearScope(HookCollection ownedHooks)
    {
        switch (ownedHooks.Role)
        {
            case HookCollectionRole.SuppressThrowaway:
                if (_current.Value is null)
                {
                    _current.Value = ownedHooks.Previous;
                }
                break;

            case HookCollectionRole.Owning:
                if (ReferenceEquals(_current.Value, ownedHooks))
                {
                    _current.Value = ownedHooks.Previous;
                }
                break;

            case HookCollectionRole.Joining:
                // No-op: the outer scope owns the slot.
                break;
        }
    }
}
