using System.Diagnostics;
using System.Reflection;
using Transactional.Core.Attributes;
using Transactional.Core.Hooks;
using Transactional.Core.Observability;
using System.Transactions;

namespace Transactional.Core.Proxy;

/// <summary>
/// Per-call state passed from TransactionProxy&lt;T&gt; to TransactionScopeExecutor.
/// Hooks is always non-null; callers check HasHooksFor before iterating.
/// An empty collection means this invocation is joining an existing scope or is Suppress —
/// hooks registered during the call flow into the ambient (parent) collection instead.
///
/// Stopwatch ownership: started in OpenScope, stopped in Commit or Rollback.
/// After either of those returns, Elapsed is stable — callers must not restart or stop it.
/// </summary>
internal sealed class TransactionContext(
    MethodInfo method,
    TransactionScope scope,
    TransactionalAttribute attr,
    Stopwatch stopwatch,
    ITransactionLifecycleObserver observer,
    HookCollection hooks)
{
    internal MethodInfo                    Method    { get; } = method;
    internal TransactionScope              Scope     { get; } = scope;
    internal TransactionalAttribute        Attr      { get; } = attr;
    internal Stopwatch                     Stopwatch { get; } = stopwatch;
    internal ITransactionLifecycleObserver Observer  { get; } = observer;
    internal HookCollection                Hooks     { get; } = hooks;
}
