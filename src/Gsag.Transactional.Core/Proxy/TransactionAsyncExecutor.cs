using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using Gsag.Transactional.Core.Hooks;

namespace Gsag.Transactional.Core.Proxy;

internal static class TransactionAsyncExecutor
{
    // Sentinel type — allows void and result-returning paths to share WrapCoreAsync<TResult>.
    // VoidAdapter converts ValueTask → ValueTask<VoidResult> so both paths enter the same template.
    private readonly struct VoidResult { }

    // -------------------------------------------------------------------------
    // Public adapters — convert the concrete awaitable type and delegate to WrapCoreAsync.
    // Task-returning adapters call .AsTask() so callers that hold a Task stay on that path.
    // -------------------------------------------------------------------------

    internal static Task ExecuteAsync(Task task, TransactionContext ctx) =>
        WrapCoreAsync(VoidAdapter(new ValueTask(task)), ctx).AsTask();

    internal static async ValueTask ExecuteAsync(ValueTask vt, TransactionContext ctx)
    {
        await WrapCoreAsync(VoidAdapter(vt), ctx).ConfigureAwait(false);
    }

    internal static Task<TResult> ExecuteAsync<TResult>(Task<TResult> task, TransactionContext ctx) =>
        WrapCoreAsync(new ValueTask<TResult>(task), ctx).AsTask(); // called via TransactionDelegateCache

    internal static ValueTask<TResult> ExecuteAsync<TResult>(ValueTask<TResult> vt, TransactionContext ctx) =>
        WrapCoreAsync(vt, ctx); // called via TransactionDelegateCache

    private static async ValueTask<VoidResult> VoidAdapter(ValueTask vt)
    {
        await vt.ConfigureAwait(false);
        return default;
    }

    // -------------------------------------------------------------------------
    // Core template — single point of ownership for the full async transaction lifecycle.
    //
    // outcome: starts as RolledBack and is promoted to Committed or CommittedWithException
    // only after Commit() returns successfully. Ensures hooks never fire on the wrong path
    // and that hook failures are suppressed whenever an exception is already propagating.
    // NotifyCommitOutcome: called after TryDispose so the observer only receives OnCommit when
    // Dispose confirms no error. If Dispose throws, the observer receives OnRollback instead.
    // -------------------------------------------------------------------------

    [SuppressMessage("Major Code Smell", "S1854", Justification = "outcome is read in the finally block; Sonar cannot track flow across a catch-then-throw boundary into finally.")]
    private static async ValueTask<TResult> WrapCoreAsync<TResult>(ValueTask<TResult> vt, TransactionContext ctx)
    {
        var outcome = TransactionOutcome.RolledBack;
        TResult result = default!;
        try
        {
            try
            {
                result = await vt.ConfigureAwait(false);
            }
            catch (Exception ex) when (ctx.Policy.ShouldRollback(ex))
            {
                await TransactionHooks.RunBeforeRollbackHooksAsync(ctx.Hooks).ConfigureAwait(false);
                TransactionLifecycle.Rollback(ctx, ex);
                throw;
            }
            catch (Exception)
            {
                await TransactionHooks.RunBeforeCommitHooksAsync(ctx.Hooks, suppressExceptions: true).ConfigureAwait(false);
                TransactionLifecycle.Commit(ctx); // NoRollbackFor path — commit despite exception
                outcome = TransactionOutcome.CommittedWithException;
                throw;
            }
            try
            {
                await TransactionHooks.RunBeforeCommitHooksAsync(ctx.Hooks, suppressExceptions: false).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                TransactionLifecycle.Rollback(ctx, ex); // BeforeCommit failed — roll back and notify observer
                throw;
            }
            TransactionLifecycle.Commit(ctx); // success path — outside catch scope, no risk of double Complete()
            outcome = TransactionOutcome.Committed;
        }
        finally
        {
            var disposeEx = TransactionLifecycle.TryDispose(ctx);
            // effectiveOutcome drives hook dispatch — hooks must not fire AfterCommit if Dispose threw.
            // NotifyCommitOutcome receives the pre-dispose `outcome` because it already handles
            // disposeEx internally (calls OnRollback instead of OnCommit when disposeEx is not null).
            var effectiveOutcome = disposeEx is not null ? TransactionOutcome.RolledBack : outcome;
            TransactionLifecycle.NotifyCommitOutcome(ctx, outcome, disposeEx);
            await TransactionHooks.RunAsyncHooksAsync(ctx.Hooks, effectiveOutcome).ConfigureAwait(false);
            if (disposeEx is not null)
            {
                ExceptionDispatchInfo.Capture(disposeEx).Throw();
            }
        }
        return result;
    }
}
