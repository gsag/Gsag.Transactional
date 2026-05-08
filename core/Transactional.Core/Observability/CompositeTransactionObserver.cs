using System.Reflection;
using Transactional.Core.Attributes;

namespace Transactional.Core.Observability;

/// <summary>
/// Composite implementation of <see cref="ITransactionLifecycleObserver"/> that delegates to
/// a list of child observers in registration order. Allows multiple observers (logging, metrics,
/// tracing) to coexist without modifying any existing observer class.
/// </summary>
internal sealed class CompositeTransactionObserver : ITransactionLifecycleObserver
{
    private readonly IReadOnlyList<ITransactionLifecycleObserver> _observers;

    internal CompositeTransactionObserver(IReadOnlyList<ITransactionLifecycleObserver> observers)
        => _observers = observers;

    public void OnBegin(MethodInfo method, TransactionalAttribute attr)
    {
        foreach (var o in _observers)
        {
            o.OnBegin(method, attr);
        }
    }

    public void OnCommit(MethodInfo method, TimeSpan elapsed)
    {
        foreach (var o in _observers)
        {
            o.OnCommit(method, elapsed);
        }
    }

    public void OnRollback(MethodInfo method, Exception exception, TimeSpan elapsed)
    {
        foreach (var o in _observers)
        {
            o.OnRollback(method, exception, elapsed);
        }
    }

    public void OnComplete(MethodInfo method, bool committed, TimeSpan elapsed)
    {
        foreach (var o in _observers)
        {
            o.OnComplete(method, committed, elapsed);
        }
    }
}
