using System.Transactions;
using Transactional.Core.Attributes;

namespace Transactional.Core.Hooks;

/// <summary>
/// Represents the outcome of a transaction scope, used by <see cref="TransactionHooks.RunSyncHooks"/>
/// and <see cref="TransactionHooks.RunAsyncHooksAsync"/> to decide which events fire and whether
/// hook exceptions should be suppressed.
/// </summary>
internal enum TransactionOutcome
{
    /// <summary>Transaction committed normally — hook failures propagate to the caller.</summary>
    Committed,
    /// <summary>Transaction committed on the NoRollbackFor path — a business exception is already
    /// propagating, so hook failures are suppressed to avoid masking it.</summary>
    CommittedWithException,
    /// <summary>Transaction rolled back — a rollback exception may be propagating, so hook
    /// failures are suppressed to avoid masking it.</summary>
    RolledBack,
}

/// <summary>
/// Describes the role a <see cref="HookCollection"/> plays in the <see cref="AsyncLocal{T}"/> slot,
/// used by <see cref="TransactionHooks.ClearScope"/> to restore the slot correctly.
/// </summary>
internal enum HookCollectionRole
{
    /// <summary>Owns the <see cref="AsyncLocal{T}"/> slot — <c>ClearScope</c> restores <c>Previous</c>
    /// when <c>ReferenceEquals</c> confirms ownership.</summary>
    Owning,
    /// <summary>Joining a pre-existing ambient scope — <c>ClearScope</c> is a no-op; the outer
    /// scope's collection remains in the slot.</summary>
    Joining,
    /// <summary>Created for a Suppress scope — the slot was set to <c>null</c> (not to this
    /// throwaway), so <c>ClearScope</c> uses a null-check path instead of <c>ReferenceEquals</c>.</summary>
    SuppressThrowaway,
}

/// <summary>
/// Per-scope container for hooks, keyed by <see cref="HookEvent"/>.
/// Sync and async hooks are stored in separate dictionaries so the sync execution
/// path never needs to touch async delegates. Lists are allocated lazily per event.
/// Sync hooks always trigger before async hooks for the same event — if strict
/// cross-type ordering is required, combine both into a single
/// <see cref="ITransactionHooks.AfterCommit(Func{Task})"/> call.
///
/// <see cref="Previous"/> forms an implicit stack: <see cref="TransactionHooks.ClearScope"/>
/// restores the slot to whatever value was active before this scope was opened,
/// so nested scopes (RequiresNew, Suppress) never permanently clobber the outer scope's collection.
///
/// Thread safety: <see cref="HookCollection"/> is not thread-safe. Registering hooks from
/// concurrent continuations within a single [Transactional] scope is not supported.
/// </summary>
internal sealed class HookCollection
{
    /// <summary>
    /// The <see cref="AsyncLocal{T}"/> value that was active before this scope was opened.
    /// <see cref="TransactionHooks.ClearScope"/> restores it so that the outer scope's hook
    /// collection survives nested RequiresNew and Suppress calls.
    /// </summary>
    internal HookCollection? Previous { get; init; }

    /// <summary>
    /// Describes how this collection interacts with the <see cref="AsyncLocal{T}"/> slot.
    /// See <see cref="HookCollectionRole"/> for the three distinct cases.
    /// </summary>
    internal HookCollectionRole Role { get; init; }

    private readonly Dictionary<HookEvent, List<Action>>     _sync  = [];
    private readonly Dictionary<HookEvent, List<Func<Task>>> _async = [];

    internal bool HasHooksFor(HookEvent evt) =>
        (_sync.TryGetValue(evt,  out var s) && s.Count > 0) ||
        (_async.TryGetValue(evt, out var a) && a.Count > 0);

    internal void AddSync(HookEvent evt, Action action)
    {
        if (!_sync.TryGetValue(evt, out var list))
        {
            _sync[evt] = list = [];
        }
        list.Add(action);
    }

    internal void AddAsync(HookEvent evt, Func<Task> action)
    {
        if (!_async.TryGetValue(evt, out var list))
        {
            _async[evt] = list = [];
        }
        list.Add(action);
    }

    internal IReadOnlyList<Action>     SyncFor(HookEvent evt)  =>
        _sync.TryGetValue(evt,  out var l) ? l : [];

    internal IReadOnlyList<Func<Task>> AsyncFor(HookEvent evt) =>
        _async.TryGetValue(evt, out var l) ? l : [];
}

/// <summary>
/// Internal implementation of <see cref="ITransactionHooks"/>.
/// Uses <see cref="AsyncLocal{T}"/> so each concurrent execution context
/// (i.e., each HTTP request in ASP.NET Core) gets its own isolated <see cref="HookCollection"/>.
///
/// The AsyncLocal stores a nullable reference. Null means "no active scope" — hook registrations
/// are no-ops when called outside a [Transactional] method or inside a Suppress scope.
/// </summary>
internal sealed class TransactionHooks : ITransactionHooks
{
    private static readonly AsyncLocal<HookCollection?> _current = new();

    public void AfterCommit(Action action)     => _current.Value?.AddSync(HookEvent.AfterCommit, action);
    public void AfterCommit(Func<Task> action) => _current.Value?.AddAsync(HookEvent.AfterCommit, action);

    public void AfterRollback(Action action)     => _current.Value?.AddSync(HookEvent.AfterRollback, action);
    public void AfterRollback(Func<Task> action) => _current.Value?.AddAsync(HookEvent.AfterRollback, action);

    public void AfterCompletion(Action action)     => _current.Value?.AddSync(HookEvent.AfterCompletion, action);
    public void AfterCompletion(Func<Task> action) => _current.Value?.AddAsync(HookEvent.AfterCompletion, action);

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
                "Change the method return type to Task or Task<T>.");
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
        // Pre-check all events before executing any hook so that no side-effect occurs
        // before a potential NotSupportedException is thrown. All events are checked
        // regardless of outcome to fail fast consistently.
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

    private static string EventLabel(HookEvent evt) => evt switch
    {
        HookEvent.AfterCommit     => "after-commit",
        HookEvent.AfterRollback   => "after-rollback",
        HookEvent.AfterCompletion => "after-completion",
        _                         => evt.ToString()
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
