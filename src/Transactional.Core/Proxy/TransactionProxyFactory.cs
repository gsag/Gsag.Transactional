using System.Reflection;
using Transactional.Core.Observability;

namespace Transactional.Core.Proxy;

/// <summary>
/// Creates TransactionProxy instances.
/// The generic overload is preferred; the non-generic one is used by the DI extension
/// when the interface type is only known at runtime.
/// </summary>
public static class TransactionProxyFactory
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

    /// <summary>Wraps <paramref name="target"/> with a transaction-intercepting proxy.</summary>
    public static TInterface Create<TInterface>(
        TInterface target,
        ITransactionLifecycleObserver? observer = null) where TInterface : class
        => TransactionProxy<TInterface>.Wrap(target, observer);

    /// <summary>
    /// Non-generic overload for runtime use (e.g., DI registration loops).
    /// Resolves into <see cref="Create{TInterface}"/> via a cached MakeGenericMethod call.
    /// </summary>
    public static object Create(
        Type interfaceType,
        object target,
        ITransactionLifecycleObserver? observer = null)
        => _createMethod.MakeGenericMethod(interfaceType).Invoke(null, [target, observer])!;
}
