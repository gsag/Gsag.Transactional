using System.Reflection;
using Gsag.Transactional.Core.Attributes;
using Gsag.Transactional.Core.Observability;

namespace Gsag.Transactional.Core.Proxy;

internal readonly record struct TransactionInvocationContext(
    MethodInfo Method,
    object?[] Args,
    TransactionalAttribute Attribute,
    ITransactionObserver Observer,
    Func<MethodInfo, object?[], object?> InvokeTarget);
