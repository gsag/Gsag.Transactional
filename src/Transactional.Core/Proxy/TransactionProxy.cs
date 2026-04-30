using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using Transactional.Core.Attributes;
using Transactional.Core.Observability;

namespace Transactional.Core.Proxy;

/// <summary>
/// A DispatchProxy that intercepts method calls and wraps those decorated with
/// [Transactional] inside a TransactionScope. Supports sync, Task, Task&lt;T&gt;,
/// ValueTask, and ValueTask&lt;T&gt; return types.
/// </summary>
public class TransactionProxy<T> : DispatchProxy where T : class
{
    private T _target = null!;
    private ITransactionLifecycleObserver _observer = NullTransactionObserver.Instance;

    // Per-T caches — static fields on a generic type are intentionally per-T instantiation.
    private static readonly ConcurrentDictionary<MethodInfo, TransactionalAttribute?> _attributeCache = new();
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

        var attr = _attributeCache.GetOrAdd(targetMethod, static m =>
        {
            var a = m.GetCustomAttribute<TransactionalAttribute>(inherit: true);
            if (a is not null)
            {
                return a;
            }

            // GetCustomAttribute does not cross interface boundaries.
            // Walk the interface map to find the attribute on the interface method.
            var declaringType = m.DeclaringType;
            if (declaringType is null)
            {
                return null;
            }

            foreach (var iface in declaringType.GetInterfaces())
            {
                var map = declaringType.GetInterfaceMap(iface);
                for (var i = 0; i < map.TargetMethods.Length; i++)
                {
                    if (map.TargetMethods[i] == m)
                    {
                        var ifaceAttr = map.InterfaceMethods[i]
                            .GetCustomAttribute<TransactionalAttribute>(inherit: true);
                        if (ifaceAttr is not null)
                        {
                            return ifaceAttr;
                        }
                    }
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
        var ctx = TransactionScopeHelper.OpenScope(method, attr, _observer);
        try
        {
            object? result;
            try
            {
                result = InvokeTarget(method, args);
            }
            catch (Exception ex) when (TransactionScopeHelper.ShouldRollback(ctx.Attr, ex))
            {
                TransactionScopeHelper.Rollback(ctx, ex);
                throw;
            }
            catch (Exception)
            {
                TransactionScopeHelper.Commit(ctx); // NoRollbackFor path — commit despite exception
                throw;
            }
            TransactionScopeHelper.Commit(ctx); // success path — outside catch scope, no risk of double Complete()
            return result;
        }
        finally
        {
            ctx.Scope.Dispose();
        }
    }

    // -------------------------------------------------------------------------
    // Async paths
    //
    // IMPORTANT: TransactionScope is created BEFORE InvokeTarget so it is ambient
    // when EF Core opens its connection and enlists. The scope is then owned by
    // the async wrapper, which calls Complete() or Dispose() after the await.
    // -------------------------------------------------------------------------

    private object HandleAsync(MethodInfo method, object?[] args, TransactionalAttribute attr, Type returnType)
    {
        var ctx = TransactionScopeHelper.OpenScope(method, attr, _observer);
        try
        {
            var task = (Task)InvokeTarget(method, args)!;
            if (returnType.IsGenericType)
            {
                var resultType = returnType.GetGenericArguments()[0];
                return TransactionScopeHelper.WrapGenericTaskMethod.MakeGenericMethod(resultType).Invoke(null, [task, ctx])!;
            }
            return TransactionScopeHelper.WrapVoidTaskAsync(task, ctx);
        }
        catch (Exception ex)
        {
            // Method threw synchronously before returning a Task — roll back and release scope.
            TransactionScopeHelper.Rollback(ctx, ex);
            ctx.Scope.Dispose();
            throw;
        }
    }

    private object HandleValueTask(MethodInfo method, object?[] args, TransactionalAttribute attr)
    {
        var ctx = TransactionScopeHelper.OpenScope(method, attr, _observer);
        try
        {
            var vt = (ValueTask)InvokeTarget(method, args)!;
            return new ValueTask(TransactionScopeHelper.WrapVoidValueTaskAsync(vt, ctx));
        }
        catch (Exception ex)
        {
            TransactionScopeHelper.Rollback(ctx, ex);
            ctx.Scope.Dispose();
            throw;
        }
    }

    private object HandleValueTaskGeneric(MethodInfo method, object?[] args, TransactionalAttribute attr, Type returnType)
    {
        var ctx = TransactionScopeHelper.OpenScope(method, attr, _observer);
        try
        {
            var vt = InvokeTarget(method, args)!;
            var resultType = returnType.GetGenericArguments()[0];
            return TransactionScopeHelper.WrapGenericValueTaskMethod.MakeGenericMethod(resultType).Invoke(null, [vt, ctx])!;
        }
        catch (Exception ex)
        {
            // Method threw synchronously before returning a ValueTask — roll back and release scope.
            TransactionScopeHelper.Rollback(ctx, ex);
            ctx.Scope.Dispose();
            throw;
        }
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
