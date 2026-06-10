using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using Gsag.Transactional.Core.Attributes;

namespace Gsag.Transactional.Core.Proxy;

// Compiled delegate caches eliminate per-call MakeGenericMethod.Invoke with object[] boxing.
// One compiled delegate per TResult — cached on first use, reused on every subsequent call.
internal static class TransactionDelegateCache
{
    private static readonly ConcurrentDictionary<Type, Func<Task, TransactionContext, Task>> _taskWrapperCache = new();
    private static readonly ConcurrentDictionary<Type, Func<object, TransactionContext, object>> _vtWrapperCache = new();
    private static readonly ConcurrentDictionary<Type, Func<Exception, Task>> _faultedTaskCache = new();
    private static readonly ConcurrentDictionary<Type, Func<Exception, object>> _faultedVtCache = new();
    private static readonly ConcurrentDictionary<MethodInfo, RollbackPolicy> _policyCache = new();

    // RollbackPolicy is constant per method (derived from an immutable attribute).
    // Caching it avoids one object allocation per transaction invocation.
    internal static RollbackPolicy GetOrCreatePolicy(MethodInfo method, TransactionalAttribute attr) =>
        _policyCache.GetOrAdd(method, static (_, a) => RollbackPolicy.From(a), attr);

    // MethodInfo looked up once — no per-call reflection overhead.
    // SingleOrDefault with parameter-type filter is required because ExecuteAsync is overloaded;
    // GetMethod(name) would throw AmbiguousMatchException with multiple overloads of the same name.
    [SuppressMessage("CodeSmell", "S125", Justification = "Multi-line comment documenting why SingleOrDefault with parameter-type filter is required — not commented-out code.")]
    [SuppressMessage("Vulnerability", "S3011", Justification = "Reflects internal methods on TransactionAsyncExecutor, an internal type in the same assembly, to build compiled MakeGenericMethod delegates.")]
    private static readonly MethodInfo ExecuteAsyncTaskMethod =
        typeof(TransactionAsyncExecutor)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .SingleOrDefault(m => m.Name == "ExecuteAsync"
                               && m.IsGenericMethodDefinition
                               && m.GetParameters()[0].ParameterType.GetGenericTypeDefinition() == typeof(Task<>))
        ?? throw new InvalidOperationException(
            "TransactionDelegateCache: generic Task<TResult> overload of 'ExecuteAsync' not found on TransactionAsyncExecutor.");

    [SuppressMessage("Vulnerability", "S3011", Justification = "Reflects internal methods on TransactionAsyncExecutor, an internal type in the same assembly, to build compiled MakeGenericMethod delegates.")]
    private static readonly MethodInfo ExecuteAsyncValueTaskMethod =
        typeof(TransactionAsyncExecutor)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .SingleOrDefault(m => m.Name == "ExecuteAsync"
                               && m.IsGenericMethodDefinition
                               && m.GetParameters()[0].ParameterType.GetGenericTypeDefinition() == typeof(ValueTask<>))
        ?? throw new InvalidOperationException(
            "TransactionDelegateCache: generic ValueTask<TResult> overload of 'ExecuteAsync' not found on TransactionAsyncExecutor.");

    internal static Task CallGenericTaskWrapper(Type tResult, Task task, TransactionContext ctx)
    {
        var del = _taskWrapperCache.GetOrAdd(tResult, static t =>
        {
            var method = ExecuteAsyncTaskMethod.MakeGenericMethod(t);
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
            var method = ExecuteAsyncValueTaskMethod.MakeGenericMethod(t);
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
