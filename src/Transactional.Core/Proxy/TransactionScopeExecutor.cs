using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Transactions;
using Transactional.Core.Attributes;
using Transactional.Core.Hooks;
using Transactional.Core.Observability;

namespace Transactional.Core.Proxy;

/// <summary>
/// Per-call state passed from TransactionProxy&lt;T&gt; to the static async wrappers below.
/// Hooks is always non-null; wrappers check HasHooksFor before iterating.
/// An empty collection means this invocation is joining an existing scope or is Suppress —
/// hooks registered during the call flow into the ambient (parent) collection instead.
/// </summary>
internal sealed class TransactionContext(
    MethodInfo method,
    TransactionScope scope,
    TransactionalAttribute attr,
    Stopwatch stopwatch,
    ITransactionLifecycleObserver observer,
    HookCollection hooks)
{
    internal MethodInfo                    Method    { get; } = method;
    internal TransactionScope              Scope     { get; } = scope;
    internal TransactionalAttribute        Attr      { get; } = attr;
    internal Stopwatch                     Stopwatch { get; } = stopwatch;
    internal ITransactionLifecycleObserver Observer  { get; } = observer;
    internal HookCollection                Hooks     { get; } = hooks;
}

/// <summary>
/// Non-generic owner of the commit/rollback/dispose skeleton and async wrappers.
/// Lives outside TransactionProxy&lt;T&gt; so the MethodInfo fields are computed once
/// per application, not once per proxied interface type.
/// </summary>
internal static class TransactionScopeExecutor
{
    // MethodInfo looked up once on a non-generic type — no per-T duplication.
    internal static readonly MethodInfo WrapGenericTaskAsyncMethod =
        typeof(TransactionScopeExecutor).GetMethod(nameof(WrapGenericTaskAsync), BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException(
            $"TransactionScopeExecutor: required helper '{nameof(WrapGenericTaskAsync)}' not found.");

    internal static readonly MethodInfo WrapGenericValueTaskAsyncMethod =
        typeof(TransactionScopeExecutor).GetMethod(nameof(WrapGenericValueTaskAsync), BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException(
            $"TransactionScopeExecutor: required helper '{nameof(WrapGenericValueTaskAsync)}' not found.");

    // -------------------------------------------------------------------------
    // Scope factory
    // -------------------------------------------------------------------------

    internal static TransactionContext OpenScope(MethodInfo method, TransactionalAttribute attr, ITransactionLifecycleObserver observer)
    {
        var sw = Stopwatch.StartNew();
        var hooks = TransactionHooks.BeginScope(attr);
        TransactionScope? scope = null;
        try
        {
            scope = CreateScope(attr);          // scope exists before observer fires
            observer.OnBegin(method, attr);
            return new TransactionContext(method, scope, attr, sw, observer, hooks);
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
                Timeout = attr.TimeoutSeconds.HasValue
                    ? TimeSpan.FromSeconds(attr.TimeoutSeconds.Value)
                    : TransactionManager.DefaultTimeout
            },
            TransactionScopeAsyncFlowOption.Enabled);

    // -------------------------------------------------------------------------
    // Lifecycle helpers
    // -------------------------------------------------------------------------

    internal static void Commit(TransactionContext ctx)
    {
        try
        {
            ctx.Scope.Complete();
        }
        catch (Exception ex)
        {
            // Complete() can throw TransactionAbortedException (e.g. timeout).
            // Treat as rollback so the observer is notified and Stopwatch is stopped.
            ctx.Stopwatch.Stop();
            ctx.Observer.OnRollback(ctx.Method, ex, ctx.Stopwatch.Elapsed);
            throw;
        }
        ctx.Stopwatch.Stop();
        ctx.Observer.OnCommit(ctx.Method, ctx.Stopwatch.Elapsed);
    }

    internal static void Rollback(TransactionContext ctx, Exception ex)
    {
        ctx.Stopwatch.Stop();
        ctx.Observer.OnRollback(ctx.Method, ex, ctx.Stopwatch.Elapsed);
    }

    /// <summary>
    /// Disposes the scope and always restores the AsyncLocal hook slot — even if Dispose throws.
    /// Returns any exception thrown by Dispose so callers can rethrow it after hooks have run,
    /// rather than letting it short-circuit hook execution.
    ///
    /// On the async path, <c>ClearScope</c> is called twice: once synchronously in
    /// <c>HandleAsync</c> / <c>HandleValueTask*</c> (to restore the caller's ExecutionContext),
    /// and again here inside the async wrapper's <c>finally</c>. The second call is harmless —
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

    /// <summary>
    /// Disposes the scope and always restores the AsyncLocal hook slot, then rethrows any Dispose
    /// exception immediately. Use in <c>catch</c> blocks where hooks are not expected to run.
    /// Use <see cref="TryDispose"/> in <c>finally</c> blocks where hooks must still fire.
    /// </summary>
    internal static void DisposeScope(TransactionContext ctx)
    {
        var ex = TryDispose(ctx);
        if (ex is not null)
        {
            ExceptionDispatchInfo.Capture(ex).Throw();
        }
    }

    // -------------------------------------------------------------------------
    // Rollback rules
    // -------------------------------------------------------------------------

    internal static bool ShouldRollback(TransactionalAttribute attr, Exception ex)
    {
        // NoRollbackFor wins: matching exception type commits despite the exception.
        if (attr.NoRollbackFor.Length > 0 && IsMatch(ex, attr.NoRollbackFor))
        {
            return false;
        }

        // RollbackFor: if specified, only rollback for these types.
        if (attr.RollbackFor.Length > 0)
        {
            return IsMatch(ex, attr.RollbackFor);
        }

        // Default: rollback on any exception.
        return true;
    }

    private static bool IsMatch(Exception ex, Type[] types) =>
        types.Any(t => t.IsAssignableFrom(ex.GetType()));

    // -------------------------------------------------------------------------
    // Async wrappers — own the TransactionScope lifetime after the method returns.
    // All commit/rollback/dispose logic is consolidated here.
    //
    // outcome: starts as RolledBack and is promoted to Committed or CommittedWithException
    // only after Commit() returns successfully. This ensures hooks never fire on the rollback path
    // and that hook failures are suppressed whenever an exception is already propagating.
    // DisposeScope: Dispose + ClearScope in one call; if Dispose throws the exception propagates
    // out of the finally before RunAsyncHooksAsync — hooks are correctly skipped.
    // -------------------------------------------------------------------------

    internal static async Task WrapVoidTaskAsync(Task task, TransactionContext ctx)
    {
        var outcome = TransactionOutcome.RolledBack;
        try
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception ex) when (ShouldRollback(ctx.Attr, ex))
            {
                Rollback(ctx, ex);
                throw;
            }
            catch (Exception)
            {
                Commit(ctx); // NoRollbackFor path — commit despite exception
                outcome = TransactionOutcome.CommittedWithException;
                throw;
            }
            Commit(ctx); // success path — outside catch scope, no risk of double Complete()
            outcome = TransactionOutcome.Committed;
        }
        finally
        {
            var disposeEx = TryDispose(ctx);
            var effectiveOutcome = disposeEx is not null ? TransactionOutcome.RolledBack : outcome;
            await TransactionHooks.RunAsyncHooksAsync(ctx.Hooks, effectiveOutcome).ConfigureAwait(false);
            if (disposeEx is not null)
            {
                ExceptionDispatchInfo.Capture(disposeEx).Throw();
            }
        }
    }

    internal static async ValueTask WrapVoidValueTaskAsync(ValueTask vt, TransactionContext ctx)
    {
        var outcome = TransactionOutcome.RolledBack;
        try
        {
            try
            {
                await vt.ConfigureAwait(false);
            }
            catch (Exception ex) when (ShouldRollback(ctx.Attr, ex))
            {
                Rollback(ctx, ex);
                throw;
            }
            catch (Exception)
            {
                Commit(ctx); // NoRollbackFor path — commit despite exception
                outcome = TransactionOutcome.CommittedWithException;
                throw;
            }
            Commit(ctx); // success path — outside catch scope, no risk of double Complete()
            outcome = TransactionOutcome.Committed;
        }
        finally
        {
            var disposeEx = TryDispose(ctx);
            var effectiveOutcome = disposeEx is not null ? TransactionOutcome.RolledBack : outcome;
            await TransactionHooks.RunAsyncHooksAsync(ctx.Hooks, effectiveOutcome).ConfigureAwait(false);
            if (disposeEx is not null)
            {
                ExceptionDispatchInfo.Capture(disposeEx).Throw();
            }
        }
    }

    // Called via reflection (MakeGenericMethod).
    private static async Task<TResult> WrapGenericTaskAsync<TResult>(Task<TResult> task, TransactionContext ctx)
    {
        var outcome = TransactionOutcome.RolledBack;
        TResult result;
        try
        {
            try
            {
                result = await task.ConfigureAwait(false);
            }
            catch (Exception ex) when (ShouldRollback(ctx.Attr, ex))
            {
                Rollback(ctx, ex);
                throw;
            }
            catch (Exception)
            {
                Commit(ctx); // NoRollbackFor path — commit despite exception
                outcome = TransactionOutcome.CommittedWithException;
                throw;
            }
            Commit(ctx); // success path — outside catch scope, no risk of double Complete()
            outcome = TransactionOutcome.Committed;
        }
        finally
        {
            var disposeEx = TryDispose(ctx);
            var effectiveOutcome = disposeEx is not null ? TransactionOutcome.RolledBack : outcome;
            await TransactionHooks.RunAsyncHooksAsync(ctx.Hooks, effectiveOutcome).ConfigureAwait(false);
            if (disposeEx is not null)
            {
                ExceptionDispatchInfo.Capture(disposeEx).Throw();
            }
        }
        return result;
    }

    // Called via reflection (MakeGenericMethod). Awaits ValueTask<TResult> directly
    // to avoid the AsTask() allocation on the already-completed fast path.
    private static async ValueTask<TResult> WrapGenericValueTaskAsync<TResult>(ValueTask<TResult> vt, TransactionContext ctx)
    {
        var outcome = TransactionOutcome.RolledBack;
        TResult result;
        try
        {
            try
            {
                result = await vt.ConfigureAwait(false);
            }
            catch (Exception ex) when (ShouldRollback(ctx.Attr, ex))
            {
                Rollback(ctx, ex);
                throw;
            }
            catch (Exception)
            {
                Commit(ctx); // NoRollbackFor path — commit despite exception
                outcome = TransactionOutcome.CommittedWithException;
                throw;
            }
            Commit(ctx); // success path — outside catch scope, no risk of double Complete()
            outcome = TransactionOutcome.Committed;
        }
        finally
        {
            var disposeEx = TryDispose(ctx);
            var effectiveOutcome = disposeEx is not null ? TransactionOutcome.RolledBack : outcome;
            await TransactionHooks.RunAsyncHooksAsync(ctx.Hooks, effectiveOutcome).ConfigureAwait(false);
            if (disposeEx is not null)
            {
                ExceptionDispatchInfo.Capture(disposeEx).Throw();
            }
        }
        return result;
    }
}
