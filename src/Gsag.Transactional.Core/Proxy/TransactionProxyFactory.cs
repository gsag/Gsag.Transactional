using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using Gsag.Transactional.Core.Observability;

namespace Gsag.Transactional.Core.Proxy;

/// <summary>
/// Creates TransactionProxy instances.
/// The generic overload is preferred; the non-generic one is used by the DI extension
/// when the interface type is only known at runtime.
/// </summary>
internal static class TransactionProxyFactory
{
    // Cached once — pinned by generic arity and parameter count to survive future overloads.
    private static readonly MethodInfo _createMethod =
        typeof(TransactionProxyFactory)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .SingleOrDefault(m =>
                m.IsGenericMethodDefinition
                && m.Name == nameof(Create)
                && m.GetGenericArguments().Length == 1
                && m.GetParameters().Length == 2)
        ?? throw new InvalidOperationException(
            $"{nameof(TransactionProxyFactory)}.{nameof(Create)}<TInterface> (2 params) not found via reflection.");

    // Compiled delegate cache — one entry per interface type seen at runtime.
    // Eliminates per-call MakeGenericMethod + object[] boxing on the DI hot path.
    private static readonly ConcurrentDictionary<Type, Func<object, ITransactionObserver?, object>> _createDelegates = new();

    /// <summary>Wraps <paramref name="target"/> with a transaction-intercepting proxy.</summary>
    public static TInterface Create<TInterface>(
        TInterface target,
        ITransactionObserver? observer = null) where TInterface : class
        => TransactionProxy<TInterface>.Wrap(target, observer);

    /// <summary>
    /// Non-generic overload for runtime use (e.g., DI registration loops).
    /// Resolves into <see cref="Create{TInterface}"/> via a compiled delegate cached per interface type.
    /// </summary>
    public static object Create(
        Type interfaceType,
        object target,
        ITransactionObserver? observer = null)
    {
        var del = _createDelegates.GetOrAdd(interfaceType, static t =>
        {
            var method = _createMethod.MakeGenericMethod(t);
            var pTarget = Expression.Parameter(typeof(object), "target");
            var pObserver = Expression.Parameter(typeof(ITransactionObserver), "observer");
            var call = Expression.Call(method, Expression.Convert(pTarget, t), pObserver);
            return Expression.Lambda<Func<object, ITransactionObserver?, object>>(
                Expression.Convert(call, typeof(object)), pTarget, pObserver).Compile();
        });
        return del(target, observer);
    }
}
