using System.Reflection;
using Transactional.Core.Attributes;
using Transactional.Core.Observability;

namespace Transactional.Tests.Unit;

public class RecordingObserver : ITransactionLifecycleObserver
{
    public List<string> Calls { get; } = [];

    public void OnBegin(MethodInfo method, TransactionalAttribute attr) =>
        Calls.Add($"BEGIN:{method.Name}");

    public void OnCommit(MethodInfo method, TimeSpan elapsed) =>
        Calls.Add($"COMMIT:{method.Name}");

    public void OnRollback(MethodInfo method, Exception exception, TimeSpan elapsed) =>
        Calls.Add($"ROLLBACK:{method.Name}");
}
