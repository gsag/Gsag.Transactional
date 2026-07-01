namespace Gsag.Transactional.Core.Proxy;

internal sealed class ValueTaskGenericInvocationStrategy : ITransactionInvocationStrategy
{
    public bool CanHandle(Type returnType)
    {
        return returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(ValueTask<>);
    }

    public object? Invoke(TransactionInvocationContext context)
    {
        return AsyncHandler.ExecuteValueTaskGeneric(
            context.Method,
            context.Args,
            context.Attribute,
            context.Method.ReturnType,
            context.Observer,
            context.InvokeTarget);
    }
}