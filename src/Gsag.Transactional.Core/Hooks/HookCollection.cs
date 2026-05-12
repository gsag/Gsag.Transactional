namespace Gsag.Transactional.Core.Hooks;

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
    /// The <see cref="System.Threading.AsyncLocal{T}"/> value that was active before this scope was opened.
    /// <see cref="TransactionHooks.ClearScope"/> restores it so that the outer scope's hook
    /// collection survives nested RequiresNew and Suppress calls.
    /// </summary>
    internal HookCollection? Previous { get; init; }

    /// <summary>
    /// Describes how this collection interacts with the <see cref="System.Threading.AsyncLocal{T}"/> slot.
    /// See <see cref="HookCollectionRole"/> for the three distinct cases.
    /// </summary>
    internal HookCollectionRole Role { get; init; }

    private Dictionary<HookEvent, List<Action>>?     _sync;
    private Dictionary<HookEvent, List<Func<Task>>>? _async;

    internal bool HasHooksFor(HookEvent evt) =>
        (_sync  is not null && _sync.TryGetValue(evt,  out var s) && s.Count > 0) ||
        (_async is not null && _async.TryGetValue(evt, out var a) && a.Count > 0);

    internal void AddSync(HookEvent evt, Action action)
    {
        _sync ??= [];
        if (!_sync.TryGetValue(evt, out var list))
        {
            _sync[evt] = list = [];
        }
        list.Add(action);
    }

    internal void AddAsync(HookEvent evt, Func<Task> action)
    {
        _async ??= [];
        if (!_async.TryGetValue(evt, out var list))
        {
            _async[evt] = list = [];
        }
        list.Add(action);
    }

    internal IReadOnlyList<Action>     SyncFor(HookEvent evt)  =>
        _sync  is not null && _sync.TryGetValue(evt,  out var l) ? l : [];

    internal IReadOnlyList<Func<Task>> AsyncFor(HookEvent evt) =>
        _async is not null && _async.TryGetValue(evt, out var l) ? l : [];
}
