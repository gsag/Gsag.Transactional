using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Gsag.Transactional.Core.Proxy;

internal static class UnsupportedAsyncLikeDetector
{
    internal static bool IsUnsupportedAsyncLikeReturnType(Type returnType)
    {
        return IsAsyncEnumerable(returnType) || HasInstanceAwaiterPattern(returnType);
    }

    private static bool IsAsyncEnumerable(Type returnType)
    {
        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>))
        {
            return true;
        }

        return returnType.GetInterfaces().Any(static interfaceType =>
            interfaceType.IsGenericType &&
            interfaceType.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>));
    }

    private static bool HasInstanceAwaiterPattern(Type returnType)
    {
        if (!TryGetInstanceGetAwaiterMethod(returnType, out var getAwaiter))
        {
            return false;
        }

        return IsAwaiterType(getAwaiter.ReturnType);
    }

    private static bool TryGetInstanceGetAwaiterMethod(
        Type returnType,
        [NotNullWhen(true)] out MethodInfo? getAwaiter)
    {
        getAwaiter = returnType.GetMethod(
            "GetAwaiter",
            BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            Type.EmptyTypes,
            modifiers: null);

        return getAwaiter is not null;
    }

    private static bool IsAwaiterType(Type awaiterType)
    {
        return awaiterType.GetMethod(
                "GetResult",
                BindingFlags.Public | BindingFlags.Instance,
                binder: null,
                Type.EmptyTypes,
                modifiers: null) is not null
            && typeof(INotifyCompletion).IsAssignableFrom(awaiterType);
    }
}