namespace Gsag.Transactional.Core.Proxy;

internal sealed class UnsupportedAsyncLikeInvocationStrategy : ITransactionInvocationStrategy
{
    public bool CanHandle(Type returnType)
    {
        return UnsupportedAsyncLikeDetector.IsUnsupportedAsyncLikeReturnType(returnType);
    }

    public object? Invoke(TransactionInvocationContext context)
    {
        var returnType = context.Method.ReturnType;

        System.Diagnostics.Trace.TraceWarning(
            "[Transactional] skipped for {0}.{1} returning {2}. " +
            "The method will run without TransactionScope because this return type is async-like " +
            "but not supported by the transactional proxy. Use Task, Task<T>, ValueTask, or ValueTask<T> " +
            "for transactional behavior.",
            context.Method.DeclaringType?.FullName,
            context.Method.Name,
            returnType.FullName ?? returnType.Name);

        return context.InvokeTarget(context.Method, context.Args);
    }
}
