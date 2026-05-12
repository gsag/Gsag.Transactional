using System.Transactions;

namespace Gsag.Transactional.Core.Attributes;

/// <summary>
/// Marks a method as transactional. The dynamic proxy wraps the call in a
/// TransactionScope — committing on success and rolling back on exception.
/// </summary>
// Inherited = false: the two-step lookup in TransactionProxy<T> (interface method → concrete method
// via GetInterfaceMap) provides effective "inheritance" without relying on runtime attribute inheritance,
// which does not cross interface boundaries anyway.
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class TransactionalAttribute : Attribute
{
    /// <summary>Isolation level for the transaction. Default: ReadCommitted.</summary>
    public IsolationLevel IsolationLevel { get; init; } = IsolationLevel.ReadCommitted;

    /// <summary>Transaction timeout in seconds. Null means the system default.</summary>
    public int? TimeoutSeconds { get; init; }

    /// <summary>
    /// Controls how this transaction interacts with an existing ambient transaction.
    ///
    ///   Required    (default) — join the ambient transaction if one exists; create a new one otherwise.
    ///   RequiresNew — always start a fresh independent transaction, suspending any ambient one.
    ///   Suppress    — execute outside any transaction, suspending the ambient one temporarily.
    ///
    /// Mirrors Spring's @Transactional propagation attribute.
    /// </summary>
    public TransactionScopeOption Propagation { get; init; } = TransactionScopeOption.Required;

    /// <summary>
    /// Exception types that trigger rollback. When empty (default), any exception causes rollback.
    /// If non-empty, only listed types (or their subclasses) trigger rollback.
    /// If a type appears in both <see cref="RollbackFor"/> and <see cref="NoRollbackFor"/>,
    /// <see cref="NoRollbackFor"/> takes precedence and the transaction commits.
    /// </summary>
    public Type[] RollbackFor { get; init; } = [];

    /// <summary>
    /// Exception types that do NOT trigger rollback — the transaction commits even when
    /// the method throws one of these. <see cref="OperationCanceledException"/> is a
    /// common candidate (cancelled requests should not always mean data corruption).
    /// Takes precedence over <see cref="RollbackFor"/> when a type appears in both arrays.
    /// </summary>
    public Type[] NoRollbackFor { get; init; } = [];
}
