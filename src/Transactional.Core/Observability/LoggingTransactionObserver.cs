using System.Reflection;
using Microsoft.Extensions.Logging;
using Transactional.Core.Attributes;

namespace Transactional.Core.Observability;

/// <summary>
/// Default ITransactionLifecycleObserver that writes structured log entries.
/// Register via AddTransactionalLogging() to enable it across all proxied services.
/// </summary>
public sealed class LoggingTransactionObserver : ITransactionLifecycleObserver
{
    private readonly ILogger<LoggingTransactionObserver> _logger;

    public LoggingTransactionObserver(ILogger<LoggingTransactionObserver> logger)
        => _logger = logger;

    public void OnBegin(MethodInfo method, TransactionalAttribute attr) =>
        _logger.LogDebug(
            "Transaction BEGIN  — {Method} (isolation={Isolation}, propagation={Propagation})",
            method.Name, attr.IsolationLevel, attr.Propagation);

    public void OnCommit(MethodInfo method, TimeSpan elapsed) =>
        _logger.LogDebug(
            "Transaction COMMIT — {Method} ({Ms} ms)",
            method.Name, (long)elapsed.TotalMilliseconds);

    public void OnRollback(MethodInfo method, Exception exception, TimeSpan elapsed) =>
        _logger.LogWarning(exception,
            "Transaction ROLLBACK — {Method} ({Ms} ms) [{ExceptionType}: {ExceptionMessage}]",
            method.Name, (long)elapsed.TotalMilliseconds,
            exception.GetType().Name, exception.Message);
}
