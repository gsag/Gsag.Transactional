using System.Diagnostics;
using System.Reflection;
using System.Transactions;
using Gsag.Transactional.Core.Attributes;
using Gsag.Transactional.Core.Hooks;
using Gsag.Transactional.Core.Observability;

namespace Gsag.Transactional.Core.Proxy;

internal static class TransactionScopeFactory
{
    internal static TransactionContext OpenScope(MethodInfo method, TransactionalAttribute attr, ITransactionObserver observer)
    {
        var sw = Stopwatch.StartNew();
        var hooks = TransactionHooks.BeginScope(attr);
        TransactionScope? scope = null;
        try
        {
            scope = CreateScope(attr); // scope exists before observer fires
            var ctx = new TransactionContext(method, scope, attr, sw, observer, hooks);
            observer.OnBegin(ctx.Info);
            return ctx;
        }
        catch
        {
            scope?.Dispose();
            TransactionHooks.ClearScope(hooks); // restore AsyncLocal on any failure
            throw;
        }
    }

    private static TransactionScope CreateScope(TransactionalAttribute attr) =>
        new(
            attr.Propagation,
            new TransactionOptions
            {
                IsolationLevel = attr.IsolationLevel,
                Timeout = attr.TimeoutSeconds > 0
                    ? TimeSpan.FromSeconds(attr.TimeoutSeconds)
                    : TransactionManager.DefaultTimeout
            },
            TransactionScopeAsyncFlowOption.Enabled);
}
