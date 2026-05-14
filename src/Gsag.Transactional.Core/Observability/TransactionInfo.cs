using System.Transactions;

namespace Gsag.Transactional.Core.Observability;

/// <summary>
/// Stable public snapshot of a transaction's configuration, passed to
/// <see cref="ITransactionObserver"/> at every lifecycle event.
/// Decouples observer implementations from <see cref="System.Reflection.MethodInfo"/>
/// and from the proxy's internal wiring.
/// </summary>
public sealed record TransactionInfo
{
    /// <summary>Unqualified name of the method that opened the transaction scope.</summary>
    public string MethodName { get; init; } = string.Empty;

    /// <summary>The type that declares the method.</summary>
    public Type DeclaringType { get; init; } = typeof(object);

    /// <summary>Isolation level the scope was opened with.</summary>
    public IsolationLevel IsolationLevel { get; init; }

    /// <summary>Propagation behaviour (<c>Required</c>, <c>RequiresNew</c>, <c>Suppress</c>).</summary>
    public TransactionScopeOption Propagation { get; init; }

    /// <summary>Timeout in seconds, or <c>null</c> if the default applies.</summary>
    public int? TimeoutSeconds { get; init; }
}
