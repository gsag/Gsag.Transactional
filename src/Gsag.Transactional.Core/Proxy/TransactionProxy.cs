using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using Gsag.Transactional.Core.Attributes;
using Gsag.Transactional.Core.Observability;

namespace Gsag.Transactional.Core.Proxy;

/// <summary>
/// A DispatchProxy that intercepts method calls and wraps those decorated with
/// [Transactional] inside a TransactionScope. Supports sync, Task, Task&lt;T&gt;,
/// ValueTask, and ValueTask&lt;T&gt; return types. Async-like return types that the
/// proxy cannot safely manage, such as IAsyncEnumerable&lt;T&gt; and custom awaitables,
/// are invoked directly after emitting a warning.
/// </summary>
internal class TransactionProxy<T> : DispatchProxy where T : class
{
    private T _target = null!;
    private ITransactionObserver _observer = NullTransactionObserver.Instance;

    // Per-T caches: static fields on a generic type are intentionally per-T instantiation.
    // Key includes the concrete type so that different implementations of the same interface
    // each get their own attribute entries.
    [SuppressMessage("Major Code Smell", "S2743", Justification = "Per-T instantiation is the intended behaviour; each proxied interface gets its own isolated cache.")]
    private static readonly ConcurrentDictionary<(MethodInfo method, Type concrete), TransactionalAttribute?> _attributeCache = new();

    // _delegateCache key is the interface MethodInfo, not the concrete method, because
    // the Expression.Convert(instanceParam, method.DeclaringType!) in BuildDelegate targets
    // the interface, and virtual dispatch carries the call to the correct concrete override.
    // Including the concrete type in the key is unnecessary and would fragment the cache.
    [SuppressMessage("Major Code Smell", "S2743", Justification = "Per-T instantiation is the intended behaviour; each proxied interface gets its own isolated cache.")]
    private static readonly ConcurrentDictionary<MethodInfo, Func<object, object?[], object?>> _delegateCache = new();

    public static T Wrap(T target, ITransactionObserver? observer = null)
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
        args ??= Array.Empty<object?>();

        var attr = _attributeCache.GetOrAdd((targetMethod, _target.GetType()), FindAttribute);

        if (attr is null)
        {
            return InvokeTarget(targetMethod, args);
        }

        var strategy = TransactionInvocationStrategyResolver.Resolve(targetMethod.ReturnType);
        var context = new TransactionInvocationContext(targetMethod, args, attr, _observer, InvokeTarget);
        return strategy.Invoke(context);
    }

    // IL2072: concreteType comes from _target.GetType(); its interface members are not tracked
    // statically, but concreteType is always an instance of T (enforced by Wrap) and T is
    // constrained to be an interface, so the interface map is guaranteed to resolve correctly.
    [UnconditionalSuppressMessage("ReflectionAnalysis", "IL2072",
        Justification = "concreteType always implements the interface, verified at proxy creation by Wrap(). " +
                        "Interface methods are preserved because T is a generic type parameter on TransactionProxy<T>.")]
    private static TransactionalAttribute? FindAttribute((MethodInfo Method, Type Concrete) key)
    {
        var (m, concreteType) = key;

        // 1. Attribute on the interface method: checked first so interface-level
        //    declarations take precedence (e.g. library contracts, test doubles).
        var a = m.GetCustomAttribute<TransactionalAttribute>(inherit: false);
        if (a is not null)
        {
            return a;
        }

        // 2. Attribute on the concrete implementation method: DispatchProxy.Invoke
        //    always passes the interface MethodInfo, so we must resolve the concrete
        //    counterpart via the interface map.
        if (m.DeclaringType is null)
        {
            return null;
        }

        var map = concreteType.GetInterfaceMap(m.DeclaringType);
        var idx = Array.FindIndex(map.InterfaceMethods, im => im == m);
        return idx < 0
            ? null
            : map.TargetMethods[idx].GetCustomAttribute<TransactionalAttribute>(inherit: false);
    }

    private object? InvokeTarget(MethodInfo method, object?[] args)
    {
        var invoke = _delegateCache.GetOrAdd(method, BuildDelegate);
        return invoke(_target, args);
    }

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