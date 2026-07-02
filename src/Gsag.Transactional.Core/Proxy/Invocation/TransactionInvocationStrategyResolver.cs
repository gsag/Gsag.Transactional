using System.Collections.Concurrent;

namespace Gsag.Transactional.Core.Proxy;

internal static class TransactionInvocationStrategyResolver
{
    private static readonly ITransactionInvocationStrategy DefaultStrategy = new SyncInvocationStrategy();

    private static readonly ITransactionInvocationStrategy[] _strategies =
    [
        new ValueTaskInvocationStrategy(),
        new ValueTaskGenericInvocationStrategy(),
        new TaskInvocationStrategy(),
        new UnsupportedAsyncLikeInvocationStrategy()
    ];

    private static readonly ConcurrentDictionary<Type, ITransactionInvocationStrategy> _cache = new();

    internal static ITransactionInvocationStrategy Resolve(Type returnType)
    {
        return _cache.GetOrAdd(returnType, GetStrategyByType);
    }

    private static ITransactionInvocationStrategy GetStrategyByType(Type returnType)
    {
        return _strategies.FirstOrDefault(strategy => strategy.CanHandle(returnType), DefaultStrategy);
    }
}
