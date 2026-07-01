namespace Gsag.Transactional.Core.Proxy;

internal sealed class SyncInvocationStrategy : ITransactionInvocationStrategy
{
    public bool CanHandle(Type returnType)
    {
        return true;
    }

    public object? Invoke(TransactionInvocationContext context)
    {
        return SyncHandler.Execute(
            context.Method,
            context.Args,
            context.Attribute,
            context.Observer,
            context.InvokeTarget);
    }
}