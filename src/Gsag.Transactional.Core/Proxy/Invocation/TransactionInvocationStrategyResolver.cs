using System.Collections.Concurrent;

namespace Gsag.Transactional.Core.Proxy;

internal static class TransactionInvocationStrategyResolver
{
    private static readonly ITransactionInvocationStrategy[] _strategies =
    [
        new ValueTaskInvocationStrategy(),
        new ValueTaskGenericInvocationStrategy(),
        new TaskInvocationStrategy(),
        new UnsupportedAsyncLikeInvocationStrategy(),
        new SyncInvocationStrategy(),
    ];

    private static readonly ConcurrentDictionary<Type, ITransactionInvocationStrategy> _cache = new();

    internal static ITransactionInvocationStrategy Resolve(Type returnType)
    {
        return _cache.GetOrAdd(returnType, ResolveCore);
    }

    private static ITransactionInvocationStrategy ResolveCore(Type returnType)
    {
        return _strategies.First(strategy => strategy.CanHandle(returnType));
    }
}
