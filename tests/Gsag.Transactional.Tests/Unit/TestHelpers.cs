using Gsag.Transactional.Core.Observability;

namespace Gsag.Transactional.Tests.Unit;

public class RecordingObserver : ITransactionObserver
{
    public List<string> Calls { get; } = [];

    public void OnBegin(TransactionInfo info) =>
        Calls.Add($"BEGIN:{info.MethodName}");

    public void OnCommit(TransactionInfo info, TimeSpan elapsed) =>
        Calls.Add($"COMMIT:{info.MethodName}");

    public void OnRollback(TransactionInfo info, Exception exception, TimeSpan elapsed) =>
        Calls.Add($"ROLLBACK:{info.MethodName}");

    public void OnComplete(TransactionInfo info, bool committed, TimeSpan elapsed) =>
        Calls.Add($"COMPLETE:{info.MethodName}:{committed}");
}
