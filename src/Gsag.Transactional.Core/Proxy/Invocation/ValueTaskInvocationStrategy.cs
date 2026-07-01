namespace Gsag.Transactional.Core.Proxy;

internal sealed class ValueTaskInvocationStrategy : ITransactionInvocationStrategy
{
    public bool CanHandle(Type returnType)
    {
        return returnType == typeof(ValueTask);
    }

    public object? Invoke(TransactionInvocationContext context)
    {
        // ValueTask is boxed as object because DispatchProxy.Invoke must return object?.
        // The caller's generated code unboxes and awaits correctly.
#pragma warning disable S3415 // ValueTask must be boxed here due to DispatchProxy.Invoke signature constraint
        return AsyncHandler.ExecuteValueTask(
            context.Method,
            context.Args,
            context.Attribute,
            context.Observer,
            context.InvokeTarget);
#pragma warning restore S3415
    }
}
