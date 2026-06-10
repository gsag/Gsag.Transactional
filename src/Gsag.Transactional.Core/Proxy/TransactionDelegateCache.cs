using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;

namespace Gsag.Transactional.Core.Proxy;

// Compiled delegate caches eliminate per-call MakeGenericMethod.Invoke with object[] boxing.
// One compiled delegate per TResult — cached on first use, reused on every subsequent call.
internal static class TransactionDelegateCache
{
    private static readonly ConcurrentDictionary<Type, Func<Task, TransactionContext, Task>> _taskWrapperCache = new();
    private static readonly ConcurrentDictionary<Type, Func<object, TransactionContext, object>> _vtWrapperCache = new();
    private static readonly ConcurrentDictionary<Type, Func<Exception, Task>> _faultedTaskCache = new();
    private static readonly ConcurrentDictionary<Type, Func<Exception, object>> _faultedVtCache = new();

    // MethodInfo looked up once — no per-call reflection overhead.
    [SuppressMessage("Vulnerability", "S3011", Justification = "Reflects internal methods on TransactionAsyncRunner, an internal type in the same assembly, to build compiled MakeGenericMethod delegates.")]
    private static readonly MethodInfo WrapGenericTaskAsyncMethod =
        typeof(TransactionAsyncRunner).GetMethod(nameof(TransactionAsyncRunner.WrapGenericTaskAsync), BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException(
            $"TransactionDelegateCache: required helper '{nameof(TransactionAsyncRunner.WrapGenericTaskAsync)}' not found on TransactionAsyncRunner.");

    [SuppressMessage("Vulnerability", "S3011", Justification = "Reflects internal methods on TransactionAsyncRunner, an internal type in the same assembly, to build compiled MakeGenericMethod delegates.")]
    private static readonly MethodInfo WrapGenericValueTaskAsyncMethod =
        typeof(TransactionAsyncRunner).GetMethod(nameof(TransactionAsyncRunner.WrapGenericValueTaskAsync), BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException(
            $"TransactionDelegateCache: required helper '{nameof(TransactionAsyncRunner.WrapGenericValueTaskAsync)}' not found on TransactionAsyncRunner.");

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
}
