using System.Runtime.ExceptionServices;
using Gsag.Transactional.Core.Hooks;

namespace Gsag.Transactional.Core.Proxy;

internal static class TransactionLifecycle
{
    internal static void Commit(TransactionContext ctx)
    {
        try
        {
            ctx.Scope.Complete();
        }
        catch (Exception ex)
        {
            // Complete() can throw (e.g. TransactionAbortedException on DTC timeout or
            // transaction manager abort). Treat as rollback: stop the clock and call
            // OnRollback here.
            //
            // Two-path OnRollback design: if this catch fires while executing inside the
            // async wrappers' NoRollbackFor catch block, `outcome` stays RolledBack and
            // NotifyCommitOutcome returns early — no duplicate OnRollback is fired.
            // Note: with System.Transactions alone (no DTC) Complete() just sets a flag
            // and does not throw; this path is exercised by DTC timeout or IEnlistmentNotification
            // voting to abort during Prepare.
            ctx.Stopwatch.Stop();
            ctx.Observer.OnRollback(ctx.Info, ex, ctx.Stopwatch.Elapsed);
            throw;
        }
        ctx.Stopwatch.Stop();
        // OnCommit is deferred to NotifyCommitOutcome, called after TryDispose confirms
        // scope.Dispose() did not throw. If Dispose throws TransactionAbortedException,
        // the observer receives OnRollback instead of OnCommit.
    }

    internal static void Rollback(TransactionContext ctx, Exception ex)
    {
        ctx.Stopwatch.Stop();
        ctx.Observer.OnRollback(ctx.Info, ex, ctx.Stopwatch.Elapsed);
    }

    /// <summary>
    /// Called after <see cref="TryDispose"/> to notify the observer of the final commit outcome.
    /// Skipped when <paramref name="outcome"/> is <see cref="TransactionOutcome.RolledBack"/> —
    /// <see cref="Rollback"/> already called <c>OnRollback</c>.
    /// On the commit path, if <paramref name="disposeEx"/> is not null the transaction actually
    /// rolled back; the observer receives <c>OnRollback</c> instead of <c>OnCommit</c>.
    /// </summary>
    internal static void NotifyCommitOutcome(TransactionContext ctx, TransactionOutcome outcome, Exception? disposeEx)
    {
        if (outcome == TransactionOutcome.RolledBack)
        {
            ctx.Observer.OnComplete(ctx.Info, committed: false, ctx.Stopwatch.Elapsed);
            return;
        }
        if (disposeEx is null)
        {
            ctx.Observer.OnCommit(ctx.Info, ctx.Stopwatch.Elapsed);
            ctx.Observer.OnComplete(ctx.Info, committed: true, ctx.Stopwatch.Elapsed);
        }
        else
        {
            ctx.Observer.OnRollback(ctx.Info, disposeEx, ctx.Stopwatch.Elapsed);
            ctx.Observer.OnComplete(ctx.Info, committed: false, ctx.Stopwatch.Elapsed);
        }
    }

    /// <summary>
    /// Disposes the scope and always restores the AsyncLocal hook slot — even if Dispose throws.
    /// Returns any exception thrown by Dispose so callers can rethrow it after hooks have run,
    /// rather than letting it short-circuit hook execution.
    ///
    /// On the async path, <c>ClearScope</c> is called twice: once synchronously in
    /// <c>AsyncHandler</c> (to restore the caller's ExecutionContext), and again here inside
    /// the async wrapper's <c>finally</c>. The second call is harmless —
    /// <c>ReferenceEquals</c> / null-checks guard against restoring the wrong value.
    /// </summary>
    internal static Exception? TryDispose(TransactionContext ctx)
    {
        Exception? ex = null;
        try
        {
            ctx.Scope.Dispose();
        }
        catch (Exception e)
        {
            ex = e;
        }
        finally
        {
            TransactionHooks.ClearScope(ctx.Hooks);
        }
        return ex;
    }
}
