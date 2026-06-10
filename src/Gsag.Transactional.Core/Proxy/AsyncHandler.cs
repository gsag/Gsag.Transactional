using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Gsag.Transactional.Core.Attributes;
using Gsag.Transactional.Core.Hooks;
using Gsag.Transactional.Core.Observability;

namespace Gsag.Transactional.Core.Proxy;

// IMPORTANT: TransactionScope is created BEFORE invokeTarget so it is ambient
// when EF Core opens its connection and enlists. The scope is then owned by
// the async wrapper, which calls Complete() or Dispose() after the await.
//
// ClearScope is called synchronously after invokeTarget returns so that the caller's
// ExecutionContext sees the previous AsyncLocal value when it resumes after awaiting the
// returned task. AsyncLocal changes inside async methods propagate downward (child copies)
// but not upward — calling ClearScope from inside the wrapper's finally would restore the
// value only in the wrapper's own context, not in the outer async state machine's context.
internal static class AsyncHandler
{
    internal static Task ExecuteTask(
        MethodInfo method,
        object?[] args,
        TransactionalAttribute attr,
        Type returnType,
        ITransactionObserver observer,
        Func<MethodInfo, object?[], object?> invokeTarget)
    {
        var ctx = TransactionScopeFactory.OpenScope(method, attr, observer);
        Task task;
        try
        {
            task = (Task)invokeTarget(method, args)!;
        }
        catch (Exception ex)
        {
            // invokeTarget threw before returning its task — convert to a pre-faulted task so
            // the normal async wrapper runs the full rollback lifecycle (BeforeRollback hooks,
            // observer notifications, AfterRollback/AfterCompletion hooks) without duplication.
            task = returnType.IsGenericType
                ? TransactionDelegateCache.CreateFaultedTask(returnType.GetGenericArguments()[0], ex)
                : Task.FromException(ex);
        }
        TransactionHooks.ClearScope(ctx.Hooks); // restore _current in caller's context
        if (returnType.IsGenericType)
        {
            return TransactionDelegateCache.CallGenericTaskWrapper(returnType.GetGenericArguments()[0], task, ctx);
        }
        return TransactionAsyncExecutor.ExecuteAsync(task, ctx);
    }

    [SuppressMessage("Code Smell", "S3981", Justification = "ValueTask is immediately passed to ExecuteAsync as an argument; the try-catch wrapper is necessary to preserve rollback semantics when invokeTarget throws before returning the task.")]
    internal static ValueTask ExecuteValueTask(
        MethodInfo method,
        object?[] args,
        TransactionalAttribute attr,
        ITransactionObserver observer,
        Func<MethodInfo, object?[], object?> invokeTarget)
    {
        var ctx = TransactionScopeFactory.OpenScope(method, attr, observer);
        ValueTask vt;
        try
        {
            vt = (ValueTask)invokeTarget(method, args)!;
        }
        catch (Exception ex)
        {
            vt = ValueTask.FromException(ex);
        }
        TransactionHooks.ClearScope(ctx.Hooks); // restore _current in caller's context
        return TransactionAsyncExecutor.ExecuteAsync(vt, ctx);
    }

    [SuppressMessage("Code Smell", "S3981", Justification = "ValueTask is immediately passed to CallGenericValueTaskWrapper as an argument; the try-catch wrapper is necessary to preserve rollback semantics when invokeTarget throws before returning the task.")]
    internal static object ExecuteValueTaskGeneric(
        MethodInfo method,
        object?[] args,
        TransactionalAttribute attr,
        Type returnType,
        ITransactionObserver observer,
        Func<MethodInfo, object?[], object?> invokeTarget)
    {
        var ctx = TransactionScopeFactory.OpenScope(method, attr, observer);
        var resultType = returnType.GetGenericArguments()[0];
        object vt;
        try
        {
            vt = invokeTarget(method, args)!;
        }
        catch (Exception ex)
        {
            vt = TransactionDelegateCache.CreateFaultedValueTask(resultType, ex);
        }
        TransactionHooks.ClearScope(ctx.Hooks); // restore _current in caller's context
        return TransactionDelegateCache.CallGenericValueTaskWrapper(resultType, vt, ctx);
    }
}
