using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Gsag.Transactional.Core.Attributes;
using Gsag.Transactional.Core.Hooks;
using Gsag.Transactional.Core.Observability;

namespace Gsag.Transactional.Core.Proxy;

internal static class SyncHandler
{
    [SuppressMessage("Major Code Smell", "S1854", Justification = "outcome is read in the finally block; Sonar cannot track flow across a catch-then-throw boundary into finally.")]
    internal static object? Execute(
        MethodInfo method,
        object?[] args,
        TransactionalAttribute attr,
        ITransactionObserver observer,
        Func<MethodInfo, object?[], object?> invokeTarget)
    {
        var ctx = TransactionScopeFactory.OpenScope(method, attr, observer);
        var outcome = TransactionOutcome.RolledBack;
        try
        {
            object? result = default;
            try
            {
                result = invokeTarget(method, args);
            }
            catch (Exception ex) when (ctx.Policy.ShouldRollback(ex))
            {
                TransactionHooks.RunBeforeRollbackSyncHooks(ctx.Hooks);
                TransactionLifecycle.Rollback(ctx, ex);
                throw;
            }
            catch (Exception)
            {
                TransactionHooks.RunBeforeCommitSyncHooks(ctx.Hooks, suppressExceptions: true);
                TransactionLifecycle.Commit(ctx); // NoRollbackFor path — commit despite exception
                outcome = TransactionOutcome.CommittedWithException;
                throw;
            }
            try
            {
                TransactionHooks.RunBeforeCommitSyncHooks(ctx.Hooks, suppressExceptions: false);
            }
            catch (Exception ex)
            {
                TransactionLifecycle.Rollback(ctx, ex); // BeforeCommit failed — notify observer
                throw;
            }
            TransactionLifecycle.Commit(ctx); // success path — outside catch scope, no risk of double Complete()
            outcome = TransactionOutcome.Committed;
            return result;
        }
        finally
        {
            var disposeEx = TransactionLifecycle.TryDispose(ctx);
            var effectiveOutcome = disposeEx is not null ? TransactionOutcome.RolledBack : outcome;
            // NotifyCommitOutcome is called after Dispose so the observer only receives OnCommit
            // when scope.Dispose() confirms the transaction committed without error.
            TransactionLifecycle.NotifyCommitOutcome(ctx, outcome, disposeEx);
            TransactionHooks.RunSyncHooks(ctx.Hooks, effectiveOutcome);
            if (disposeEx is not null)
            {
                ExceptionDispatchInfo.Capture(disposeEx).Throw();
            }
        }
    }
}
