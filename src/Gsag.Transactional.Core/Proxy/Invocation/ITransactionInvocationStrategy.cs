namespace Gsag.Transactional.Core.Proxy;

internal interface ITransactionInvocationStrategy
{
    bool CanHandle(Type returnType);

    object? Invoke(TransactionInvocationContext context);
}
