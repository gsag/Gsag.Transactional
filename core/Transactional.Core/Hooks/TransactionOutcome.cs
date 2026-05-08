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
