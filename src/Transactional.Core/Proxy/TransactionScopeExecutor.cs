using System.Diagnostics;
using System.Reflection;
using System.Transactions;
using Transactional.Core.Attributes;
using Transactional.Core.Observability;

namespace Transactional.Core.Proxy;

/// <summary>
/// Per-call state passed from TransactionProxy&lt;T&gt; to the static async wrappers below.
/// </summary>
internal sealed record TransactionContext(
    MethodInfo Method,
    TransactionScope Scope,
    TransactionalAttribute Attr,
    Stopwatch Stopwatch,
    ITransactionLifecycleObserver Observer);

/// <summary>
/// Non-generic owner of the commit/rollback/dispose skeleton and async wrappers.
/// Lives outside TransactionProxy&lt;T&gt; so the MethodInfo fields are computed once
/// per application, not once per proxied interface type.
/// </summary>
internal static class TransactionScopeExecutor
{
    // MethodInfo looked up once on a non-generic type — no per-T duplication.
    internal static readonly MethodInfo WrapGenericTaskMethod =
        typeof(TransactionScopeExecutor).GetMethod(nameof(WrapGenericTaskAsync), BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException(
            $"TransactionScopeExecutor: required helper '{nameof(WrapGenericTaskAsync)}' not found.");

    internal static readonly MethodInfo WrapGenericValueTaskMethod =
        typeof(TransactionScopeExecutor).GetMethod(nameof(WrapGenericValueTaskAsync), BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException(
            $"TransactionScopeExecutor: required helper '{nameof(WrapGenericValueTaskAsync)}' not found.");

    // -------------------------------------------------------------------------
    // Scope factory
    // -------------------------------------------------------------------------

    internal static TransactionContext OpenScope(MethodInfo method, TransactionalAttribute attr, ITransactionLifecycleObserver observer)
    {
        var sw = Stopwatch.StartNew();
        observer.OnBegin(method, attr);
        return new TransactionContext(method, CreateScope(attr), attr, sw, observer);
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
        ctx.Scope.Complete();
        ctx.Stopwatch.Stop();
        ctx.Observer.OnCommit(ctx.Method, ctx.Stopwatch.Elapsed);
    }

    internal static void Rollback(TransactionContext ctx, Exception ex)
    {
        ctx.Stopwatch.Stop();
        ctx.Observer.OnRollback(ctx.Method, ex, ctx.Stopwatch.Elapsed);
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
    // -------------------------------------------------------------------------

    internal static async Task WrapVoidTaskAsync(Task task, TransactionContext ctx)
    {
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
                throw;
            }
            Commit(ctx); // success path — outside catch scope, no risk of double Complete()
        }
        finally
        {
            ctx.Scope.Dispose();
        }
    }

    internal static async Task WrapVoidValueTaskAsync(ValueTask vt, TransactionContext ctx)
    {
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
                throw;
            }
            Commit(ctx); // success path — outside catch scope, no risk of double Complete()
        }
        finally
        {
            ctx.Scope.Dispose();
        }
    }

    // Called via reflection (MakeGenericMethod).
    private static async Task<TResult> WrapGenericTaskAsync<TResult>(Task<TResult> task, TransactionContext ctx)
    {
        try
        {
            TResult result;
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
                throw;
            }
            Commit(ctx); // success path — outside catch scope, no risk of double Complete()
            return result;
        }
        finally
        {
            ctx.Scope.Dispose();
        }
    }

    // Called via reflection (MakeGenericMethod). Awaits ValueTask<TResult> directly
    // to avoid the AsTask() allocation on the already-completed fast path.
    private static async ValueTask<TResult> WrapGenericValueTaskAsync<TResult>(ValueTask<TResult> vt, TransactionContext ctx)
    {
        try
        {
            TResult result;
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
                throw;
            }
            Commit(ctx); // success path — outside catch scope, no risk of double Complete()
            return result;
        }
        finally
        {
            ctx.Scope.Dispose();
        }
    }
}
