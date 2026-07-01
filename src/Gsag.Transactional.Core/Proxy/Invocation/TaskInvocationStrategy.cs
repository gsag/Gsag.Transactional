namespace Gsag.Transactional.Core.Proxy;

internal sealed class TaskInvocationStrategy : ITransactionInvocationStrategy
{
    public bool CanHandle(Type returnType)
    {
        return typeof(Task).IsAssignableFrom(returnType);
    }

    public object? Invoke(TransactionInvocationContext context)
    {
        return AsyncHandler.ExecuteTask(
            context.Method,
            context.Args,
            context.Attribute,
            context.Method.ReturnType,
            context.Observer,
            context.InvokeTarget);
    }
}
