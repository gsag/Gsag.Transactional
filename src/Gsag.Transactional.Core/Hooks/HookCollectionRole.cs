namespace Gsag.Transactional.Core.Hooks;

/// <summary>
/// Describes the role a <see cref="HookCollection"/> plays in the <see cref="System.Threading.AsyncLocal{T}"/> slot,
/// used by <see cref="TransactionHooks.ClearScope"/> to restore the slot correctly.
/// </summary>
internal enum HookCollectionRole
{
    /// <summary>Owns the <see cref="System.Threading.AsyncLocal{T}"/> slot — <c>ClearScope</c> restores <c>Previous</c>
    /// when <c>ReferenceEquals</c> confirms ownership.</summary>
    Owning,
    /// <summary>Joining a pre-existing ambient scope — <c>ClearScope</c> is a no-op; the outer
    /// scope's collection remains in the slot.</summary>
    Joining,
    /// <summary>Created for a Suppress scope — the slot was set to <c>null</c> (not to this
    /// throwaway), so <c>ClearScope</c> uses a null-check path instead of <c>ReferenceEquals</c>.</summary>
    SuppressThrowaway,
}
