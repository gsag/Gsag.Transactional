using System.Reflection;
using Transactional.Core.Attributes;

namespace Transactional.Core.Observability;

public sealed class NullTransactionObserver : ITransactionLifecycleObserver
{
    public static readonly NullTransactionObserver Instance = new();

    public void OnBegin(MethodInfo method, TransactionalAttribute attr) { }
    public void OnCommit(MethodInfo method, TimeSpan elapsed) { }
    public void OnRollback(MethodInfo method, Exception exception, TimeSpan elapsed) { }
}
