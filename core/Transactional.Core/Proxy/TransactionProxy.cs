using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Transactional.Core.Attributes;
using Transactional.Core.Hooks;
using Transactional.Core.Observability;

namespace Transactional.Core.Proxy;

/// <summary>
/// A DispatchProxy that intercepts method calls and wraps those decorated with
/// [Transactional] inside a TransactionScope. Supports sync, Task, Task&lt;T&gt;,
/// ValueTask, and ValueTask&lt;T&gt; return types.
/// </summary>
internal class TransactionProxy<T> : DispatchProxy where T : class
{
    private T _target = null!;
    private ITransactionLifecycleObserver _observer = NullTransactionObserver.Instance;

    // Per-T caches — static fields on a generic type are intentionally per-T instantiation.
    // Key includes the concrete type so that different implementations of the same interface
    // each get their own attribute entries.
    private static readonly ConcurrentDictionary<(MethodInfo method, Type concrete), TransactionalAttribute?> _attributeCache = new();

    // _delegateCache key is the interface MethodInfo — not the concrete method — because
    // the Expression.Convert(instanceParam, method.DeclaringType!) in BuildDelegate targets
    // the interface, and virtual dispatch carries the call to the correct concrete override.
    // Including the concrete type in the key is unnecessary and would fragment the cache.
    private static readonly ConcurrentDictionary<MethodInfo, Func<object, object?[], object?>> _delegateCache = new();

    public static T Wrap(T target, ITransactionLifecycleObserver? observer = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!typeof(T).IsInterface)
        {
            throw new InvalidOperationException(
                $"TransactionProxy<T> requires T to be an interface. " +
                $"'{typeof(T).Name}' is a class. Register the interface, not the concrete type.");
        }

        var proxy = Create<T, TransactionProxy<T>>();
        var p = (TransactionProxy<T>)(object)proxy;
        p._target = target;
        p._observer = observer ?? NullTransactionObserver.Instance;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        args ??= [];

        var attr = _attributeCache.GetOrAdd((targetMethod, _target.GetType()), static key =>
        {
            var (m, concreteType) = key;

            // 1. Attribute on the interface method — checked first so interface-level
            //    declarations take precedence (e.g. library contracts, test doubles).
            var a = m.GetCustomAttribute<TransactionalAttribute>(inherit: false);
            if (a is not null)
            {
                return a;
            }

            // 2. Attribute on the concrete implementation method — DispatchProxy.Invoke
            //    always passes the interface MethodInfo, so we must resolve the concrete
            //    counterpart via the interface map.
            if (m.DeclaringType is null)
            {
                return null;
            }

            var map = concreteType.GetInterfaceMap(m.DeclaringType);
            for (var i = 0; i < map.InterfaceMethods.Length; i++)
            {
                if (map.InterfaceMethods[i] == m)
                {
                    return map.TargetMethods[i]
                        .GetCustomAttribute<TransactionalAttribute>(inherit: false);
                }
            }
            return null;
        });

        if (attr is null)
        {
            return InvokeTarget(targetMethod, args);
        }

        var returnType = targetMethod.ReturnType;

        if (returnType == typeof(ValueTask))
        {
            return HandleValueTask(targetMethod, args, attr);
        }

        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(ValueTask<>))
        {
            return HandleValueTaskGeneric(targetMethod, args, attr, returnType);
        }

        if (typeof(Task).IsAssignableFrom(returnType))
        {
            return HandleAsync(targetMethod, args, attr, returnType);
        }

        return HandleSync(targetMethod, args, attr);
    }

    // -------------------------------------------------------------------------
    // Sync path
    // -------------------------------------------------------------------------

    private object? HandleSync(MethodInfo method, object?[] args, TransactionalAttribute attr)
    {
        var ctx = TransactionScopeExecutor.OpenScope(method, attr, _observer);
        var outcome = TransactionOutcome.RolledBack;
        try
        {
            object? result;
            try
            {
                result = InvokeTarget(method, args);
            }
            catch (Exception ex) when (TransactionScopeExecutor.ShouldRollback(ctx, ex))
            {
                TransactionHooks.RunBeforeRollbackSyncHooks(ctx.Hooks);
                TransactionScopeExecutor.Rollback(ctx, ex);
                throw;
            }
            catch (Exception)
            {
                TransactionHooks.RunBeforeCommitSyncHooks(ctx.Hooks, suppressExceptions: true);
                TransactionScopeExecutor.Commit(ctx); // NoRollbackFor path — commit despite exception
                outcome = TransactionOutcome.CommittedWithException;
                throw;
            }
            try
            {
                TransactionHooks.RunBeforeCommitSyncHooks(ctx.Hooks, suppressExceptions: false);
            }
            catch (Exception ex)
            {
                TransactionScopeExecutor.Rollback(ctx, ex); // BeforeCommit failed — notify observer
                throw;
            }
            TransactionScopeExecutor.Commit(ctx); // success path — outside catch scope, no risk of double Complete()
            outcome = TransactionOutcome.Committed;
            return result;
        }
        finally
        {
            var disposeEx = TransactionScopeExecutor.TryDispose(ctx);
            var effectiveOutcome = disposeEx is not null ? TransactionOutcome.RolledBack : outcome;
            // NotifyCommitOutcome is called after Dispose so the observer only receives OnCommit
            // when scope.Dispose() confirms the transaction committed without error.
            TransactionScopeExecutor.NotifyCommitOutcome(ctx, outcome, disposeEx);
            TransactionHooks.RunSyncHooks(ctx.Hooks, effectiveOutcome);
            if (disposeEx is not null)
            {
                ExceptionDispatchInfo.Capture(disposeEx).Throw();
            }
        }
    }

    // -------------------------------------------------------------------------
    // Async paths
    //
    // IMPORTANT: TransactionScope is created BEFORE InvokeTarget so it is ambient
    // when EF Core opens its connection and enlists. The scope is then owned by
    // the async wrapper, which calls Complete() or Dispose() after the await.
    //
    // ClearScope is called synchronously after InvokeTarget returns so that the caller's
    // ExecutionContext sees the previous AsyncLocal value when it resumes after awaiting the
    // returned task. AsyncLocal changes inside async methods propagate downward (child copies)
    // but not upward — calling ClearScope from inside the wrapper's finally would restore the
    // value only in the wrapper's own context, not in the outer async state machine's context.
    // -------------------------------------------------------------------------

    private object HandleAsync(MethodInfo method, object?[] args, TransactionalAttribute attr, Type returnType)
    {
        var ctx = TransactionScopeExecutor.OpenScope(method, attr, _observer);
        Task task;
        try
        {
            task = (Task)InvokeTarget(method, args)!;
        }
        catch (Exception ex)
        {
            // InvokeTarget threw before returning its task — convert to a pre-faulted task so
            // the normal async wrapper runs the full rollback lifecycle (BeforeRollback hooks,
            // observer notifications, AfterRollback/AfterCompletion hooks) without duplication.
            task = returnType.IsGenericType
                ? TransactionScopeExecutor.CreateFaultedTask(returnType.GetGenericArguments()[0], ex)
                : Task.FromException(ex);
        }
        TransactionHooks.ClearScope(ctx.Hooks); // restore _current in caller's context
        if (returnType.IsGenericType)
        {
            return TransactionScopeExecutor.CallGenericTaskWrapper(returnType.GetGenericArguments()[0], task, ctx);
        }
        return TransactionScopeExecutor.WrapVoidTaskAsync(task, ctx);
    }

    private object HandleValueTask(MethodInfo method, object?[] args, TransactionalAttribute attr)
    {
        var ctx = TransactionScopeExecutor.OpenScope(method, attr, _observer);
        ValueTask vt;
        try
        {
            vt = (ValueTask)InvokeTarget(method, args)!;
        }
        catch (Exception ex)
        {
            vt = ValueTask.FromException(ex);
        }
        TransactionHooks.ClearScope(ctx.Hooks); // restore _current in caller's context
        return TransactionScopeExecutor.WrapVoidValueTaskAsync(vt, ctx);
    }

    private object HandleValueTaskGeneric(MethodInfo method, object?[] args, TransactionalAttribute attr, Type returnType)
    {
        var ctx = TransactionScopeExecutor.OpenScope(method, attr, _observer);
        var resultType = returnType.GetGenericArguments()[0];
        object vt;
        try
        {
            vt = InvokeTarget(method, args)!;
        }
        catch (Exception ex)
        {
            vt = TransactionScopeExecutor.CreateFaultedValueTask(resultType, ex);
        }
        TransactionHooks.ClearScope(ctx.Hooks); // restore _current in caller's context
        return TransactionScopeExecutor.CallGenericValueTaskWrapper(resultType, vt, ctx);
    }

    private object? InvokeTarget(MethodInfo method, object?[] args)
    {
        var invoke = _delegateCache.GetOrAdd(method, BuildDelegate);
        return invoke(_target, args);
    }

    /// <summary>
    /// Compiles a strongly-typed delegate to avoid MethodInfo.Invoke overhead on the hot path.
    /// </summary>
    private static Func<object, object?[], object?> BuildDelegate(MethodInfo method)
    {
        if (method.GetParameters().Any(p => p.ParameterType.IsByRef))
        {
            throw new NotSupportedException(
                $"Method '{method.Name}' on '{method.DeclaringType?.Name}' has ref/out parameters, which are not supported by TransactionProxy.");
        }

        var instanceParam = Expression.Parameter(typeof(object), "instance");
        var argsParam = Expression.Parameter(typeof(object?[]), "args");

        var callArgs = method.GetParameters()
            .Select((p, i) =>
                (Expression)Expression.Convert(
                    Expression.ArrayIndex(argsParam, Expression.Constant(i)),
                    p.ParameterType))
            .ToArray();

        var call = Expression.Call(
            Expression.Convert(instanceParam, method.DeclaringType!),
            method,
            callArgs);

        var body = method.ReturnType == typeof(void)
            ? (Expression)Expression.Block(call, Expression.Constant(null, typeof(object)))
            : Expression.Convert(call, typeof(object));

        return Expression
            .Lambda<Func<object, object?[], object?>>(body, instanceParam, argsParam)
            .Compile();
    }
}
