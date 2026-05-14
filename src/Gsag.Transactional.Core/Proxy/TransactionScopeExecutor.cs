using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Transactions;
using Gsag.Transactional.Core.Attributes;
using Gsag.Transactional.Core.Hooks;
using Gsag.Transactional.Core.Observability;

namespace Gsag.Transactional.Core.Proxy;

/// <summary>
/// Non-generic static class that owns all commit/rollback/dispose logic and async wrappers.
/// Keeping it non-generic ensures <see cref="WrapGenericTaskAsyncMethod"/> and
/// <see cref="WrapGenericValueTaskAsyncMethod"/> (MethodInfo fields used for MakeGenericMethod calls)
/// are computed once per application, not once per proxied interface type.
/// </summary>
internal static class TransactionScopeExecutor
{
    // Compiled delegate caches eliminate per-call MakeGenericMethod.Invoke with object[] boxing.
    private static readonly ConcurrentDictionary<Type, Func<Task, TransactionContext, Task>> _taskWrapperCache = new();
    private static readonly ConcurrentDictionary<Type, Func<object, TransactionContext, object>> _vtWrapperCache = new();
    private static readonly ConcurrentDictionary<Type, Func<Exception, Task>> _faultedTaskCache = new();
    private static readonly ConcurrentDictionary<Type, Func<Exception, object>> _faultedVtCache = new();

    // MethodInfo looked up once on a non-generic type — no per-T duplication.
    private static readonly MethodInfo WrapGenericTaskAsyncMethod =
        typeof(TransactionScopeExecutor).GetMethod(nameof(WrapGenericTaskAsync), BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException(
            $"TransactionScopeExecutor: required helper '{nameof(WrapGenericTaskAsync)}' not found.");

    private static readonly MethodInfo WrapGenericValueTaskAsyncMethod =
        typeof(TransactionScopeExecutor).GetMethod(nameof(WrapGenericValueTaskAsync), BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException(
            $"TransactionScopeExecutor: required helper '{nameof(WrapGenericValueTaskAsync)}' not found.");

    // -------------------------------------------------------------------------
    // Scope factory
    // -------------------------------------------------------------------------

    internal static TransactionContext OpenScope(MethodInfo method, TransactionalAttribute attr, ITransactionObserver observer)
    {
        var sw = Stopwatch.StartNew();
        var hooks = TransactionHooks.BeginScope(attr);
        TransactionScope? scope = null;
        try
        {
            scope = CreateScope(attr);          // scope exists before observer fires
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

    // -------------------------------------------------------------------------
    // Rollback rules
    // -------------------------------------------------------------------------

    internal static bool ShouldRollback(TransactionContext ctx, Exception ex)
    {
        // Snapshot to guard against the attribute arrays being replaced concurrently.
        var noRollbackFor = ctx.Attr.NoRollbackFor;
        var rollbackFor = ctx.Attr.RollbackFor;

        // NoRollbackFor wins: matching exception type commits despite the exception.
        if (noRollbackFor.Length > 0 && IsMatch(ex, noRollbackFor))
        {
            return false;
        }

        // RollbackFor: if specified, only rollback for these types.
        if (rollbackFor.Length > 0)
        {
            return IsMatch(ex, rollbackFor);
        }

        // Default: rollback on any exception.
        return true;
    }

    private static bool IsMatch(Exception ex, Type[] types) =>
        types.Any(t => t.IsAssignableFrom(ex.GetType()));

    // -------------------------------------------------------------------------
    // Compiled wrapper dispatch for generic async return types.
    // One compiled delegate per TResult — eliminates per-call MakeGenericMethod.Invoke
    // and object[] allocation on the hot async path.
    // -------------------------------------------------------------------------

    internal static Task CallGenericTaskWrapper(Type tResult, Task task, TransactionContext ctx)
    {
        var del = _taskWrapperCache.GetOrAdd(tResult, static t =>
        {
            var method = WrapGenericTaskAsyncMethod.MakeGenericMethod(t);
            var pTask = Expression.Parameter(typeof(Task), "task");
            var pCtx = Expression.Parameter(typeof(TransactionContext), "ctx");
            var call = Expression.Call(method, Expression.Convert(pTask, typeof(Task<>).MakeGenericType(t)), pCtx);
            return Expression.Lambda<Func<Task, TransactionContext, Task>>(
                Expression.Convert(call, typeof(Task)), pTask, pCtx).Compile();
        });
        return del(task, ctx);
    }

    internal static object CallGenericValueTaskWrapper(Type tResult, object vtBoxed, TransactionContext ctx)
    {
        var del = _vtWrapperCache.GetOrAdd(tResult, static t =>
        {
            var method = WrapGenericValueTaskAsyncMethod.MakeGenericMethod(t);
            var vtType = typeof(ValueTask<>).MakeGenericType(t);
            var pVt = Expression.Parameter(typeof(object), "vt");
            var pCtx = Expression.Parameter(typeof(TransactionContext), "ctx");
            var call = Expression.Call(method, Expression.Convert(pVt, vtType), pCtx);
            return Expression.Lambda<Func<object, TransactionContext, object>>(
                Expression.Convert(call, typeof(object)), pVt, pCtx).Compile();
        });
        return del(vtBoxed, ctx);
    }

    // -------------------------------------------------------------------------
    // Faulted-task factories — used when InvokeTarget throws synchronously before
    // returning its Task/ValueTask. Converting to a pre-faulted task lets the normal
    // async wrappers run the full rollback lifecycle (hooks + observer) without any
    // duplication. Compiled delegates are cached per TResult to avoid per-call reflection.
    // -------------------------------------------------------------------------

    // Returns Task<TResult>.FromException(ex), typed as Task so it can be stored alongside
    // non-generic tasks and still downcast correctly inside CallGenericTaskWrapper.
    internal static Task CreateFaultedTask(Type tResult, Exception ex) =>
        _faultedTaskCache.GetOrAdd(tResult, static t =>
        {
            var exParam = Expression.Parameter(typeof(Exception), "ex");
            var fromEx = Expression.Call(
                typeof(Task).GetMethod(nameof(Task.FromException), 1, [typeof(Exception)])!.MakeGenericMethod(t),
                exParam);
            return Expression.Lambda<Func<Exception, Task>>(fromEx, exParam).Compile();
        })(ex);

    // Returns new ValueTask<TResult>(Task<TResult>.FromException(ex)), boxed as object
    // so it can be passed to CallGenericValueTaskWrapper's object parameter.
    internal static object CreateFaultedValueTask(Type tResult, Exception ex) =>
        _faultedVtCache.GetOrAdd(tResult, static t =>
        {
            var exParam = Expression.Parameter(typeof(Exception), "ex");
            var fromEx = Expression.Call(
                typeof(Task).GetMethod(nameof(Task.FromException), 1, [typeof(Exception)])!.MakeGenericMethod(t),
                exParam);
            var vtCtor = typeof(ValueTask<>).MakeGenericType(t)
                .GetConstructor([typeof(Task<>).MakeGenericType(t)])!;
            return Expression.Lambda<Func<Exception, object>>(
                Expression.Convert(Expression.New(vtCtor, fromEx), typeof(object)),
                exParam).Compile();
        })(ex);

    // -------------------------------------------------------------------------
    // Async wrappers — own the TransactionScope lifetime after the method returns.
    //
    // outcome: starts as RolledBack and is promoted to Committed or CommittedWithException
    // only after Commit() returns successfully. Ensures hooks never fire on the wrong path
    // and that hook failures are suppressed whenever an exception is already propagating.
    // NotifyCommitOutcome: called after TryDispose so the observer only receives OnCommit when
    // Dispose confirms no error. If Dispose throws, the observer receives OnRollback instead.
    // -------------------------------------------------------------------------

    // Thin adapters — convert the concrete awaitable to ValueTask / ValueTask<TResult>
    // (struct conversions, no allocation) and delegate to the core template methods below.
    internal static Task WrapVoidTaskAsync(Task task, TransactionContext ctx) => WrapVoidCoreAsync(new ValueTask(task), ctx);
    internal static ValueTask WrapVoidValueTaskAsync(ValueTask vt, TransactionContext ctx) => new ValueTask(WrapVoidCoreAsync(vt, ctx));
    private static Task<TResult> WrapGenericTaskAsync<TResult>(Task<TResult> task, TransactionContext ctx) => WrapResultCoreAsync(new ValueTask<TResult>(task), ctx); // called via CallGenericTaskWrapper
    private static ValueTask<TResult> WrapGenericValueTaskAsync<TResult>(ValueTask<TResult> vt, TransactionContext ctx) => new ValueTask<TResult>(WrapResultCoreAsync(vt, ctx));  // called via CallGenericValueTaskWrapper

    // -------------------------------------------------------------------------
    // Core template — owns the full transaction lifecycle for void async methods.
    // -------------------------------------------------------------------------

    private static async Task WrapVoidCoreAsync(ValueTask vt, TransactionContext ctx)
    {
        var outcome = TransactionOutcome.RolledBack;
        try
        {
            try
            {
                await vt.ConfigureAwait(false);
            }
            catch (Exception ex) when (ShouldRollback(ctx, ex))
            {
                await TransactionHooks.RunBeforeRollbackHooksAsync(ctx.Hooks).ConfigureAwait(false);
                Rollback(ctx, ex);
                throw;
            }
            catch (Exception)
            {
                await TransactionHooks.RunBeforeCommitHooksAsync(ctx.Hooks, suppressExceptions: true).ConfigureAwait(false);
                Commit(ctx); // NoRollbackFor path — commit despite exception
                outcome = TransactionOutcome.CommittedWithException;
                throw;
            }
            try
            {
                await TransactionHooks.RunBeforeCommitHooksAsync(ctx.Hooks, suppressExceptions: false).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Rollback(ctx, ex); // BeforeCommit failed — roll back and notify observer
                throw;
            }
            Commit(ctx); // success path — outside catch scope, no risk of double Complete()
            outcome = TransactionOutcome.Committed;
        }
        finally
        {
            var disposeEx = TryDispose(ctx);
            var effectiveOutcome = disposeEx is not null ? TransactionOutcome.RolledBack : outcome;
            NotifyCommitOutcome(ctx, outcome, disposeEx);
            await TransactionHooks.RunAsyncHooksAsync(ctx.Hooks, effectiveOutcome).ConfigureAwait(false);
            if (disposeEx is not null)
            {
                ExceptionDispatchInfo.Capture(disposeEx).Throw();
            }
        }
    }

    // -------------------------------------------------------------------------
    // Core template — owns the full transaction lifecycle for result-returning async methods.
    // Identical to WrapVoidCoreAsync except it captures and returns the awaited result.
    // -------------------------------------------------------------------------

    private static async Task<TResult> WrapResultCoreAsync<TResult>(ValueTask<TResult> vt, TransactionContext ctx)
    {
        var outcome = TransactionOutcome.RolledBack;
        TResult result;
        try
        {
            try
            {
                result = await vt.ConfigureAwait(false);
            }
            catch (Exception ex) when (ShouldRollback(ctx, ex))
            {
                await TransactionHooks.RunBeforeRollbackHooksAsync(ctx.Hooks).ConfigureAwait(false);
                Rollback(ctx, ex);
                throw;
            }
            catch (Exception)
            {
                await TransactionHooks.RunBeforeCommitHooksAsync(ctx.Hooks, suppressExceptions: true).ConfigureAwait(false);
                Commit(ctx); // NoRollbackFor path — commit despite exception
                outcome = TransactionOutcome.CommittedWithException;
                throw;
            }
            try
            {
                await TransactionHooks.RunBeforeCommitHooksAsync(ctx.Hooks, suppressExceptions: false).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Rollback(ctx, ex);
                throw;
            }
            Commit(ctx); // success path — outside catch scope, no risk of double Complete()
            outcome = TransactionOutcome.Committed;
        }
        finally
        {
            var disposeEx = TryDispose(ctx);
            var effectiveOutcome = disposeEx is not null ? TransactionOutcome.RolledBack : outcome;
            NotifyCommitOutcome(ctx, outcome, disposeEx);
            await TransactionHooks.RunAsyncHooksAsync(ctx.Hooks, effectiveOutcome).ConfigureAwait(false);
            if (disposeEx is not null)
            {
                ExceptionDispatchInfo.Capture(disposeEx).Throw();
            }
        }
        return result;
    }
}
