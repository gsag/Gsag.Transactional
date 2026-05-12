namespace Gsag.Transactional.Core.Observability;

/// <summary>
/// Composite implementation of <see cref="ITransactionObserver"/> that delegates to
/// a list of child observers in registration order. Allows multiple observers (logging, metrics,
/// tracing) to coexist without modifying any existing observer class.
/// </summary>
internal sealed class CompositeTransactionObserver : ITransactionObserver
{
    private readonly IReadOnlyList<ITransactionObserver> _observers;

    internal CompositeTransactionObserver(IReadOnlyList<ITransactionObserver> observers)
        => _observers = observers;

    public void OnBegin(TransactionInfo info)
    {
        foreach (var o in _observers)
        {
            o.OnBegin(info);
        }
    }

    public void OnCommit(TransactionInfo info, TimeSpan elapsed)
    {
        foreach (var o in _observers)
        {
            o.OnCommit(info, elapsed);
        }
    }

    public void OnRollback(TransactionInfo info, Exception exception, TimeSpan elapsed)
    {
        foreach (var o in _observers)
        {
            o.OnRollback(info, exception, elapsed);
        }
    }

    public void OnComplete(TransactionInfo info, bool committed, TimeSpan elapsed)
    {
        foreach (var o in _observers)
        {
            o.OnComplete(info, committed, elapsed);
        }
    }
}
