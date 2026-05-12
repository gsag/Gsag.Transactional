namespace Gsag.Transactional.Core.Observability;

internal sealed class NullTransactionObserver : ITransactionObserver
{
    internal static readonly NullTransactionObserver Instance = new();

    public void OnBegin(TransactionInfo info) { }
    public void OnCommit(TransactionInfo info, TimeSpan elapsed) { }
    public void OnRollback(TransactionInfo info, Exception exception, TimeSpan elapsed) { }
    public void OnComplete(TransactionInfo info, bool committed, TimeSpan elapsed) { }
}
