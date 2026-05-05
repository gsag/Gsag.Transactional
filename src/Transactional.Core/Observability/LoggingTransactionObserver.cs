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

    /// <summary>Initializes the observer with the logger provided by the DI container.</summary>
    public LoggingTransactionObserver(ILogger<LoggingTransactionObserver> logger)
        => _logger = logger;

    /// <inheritdoc/>
    public void OnBegin(MethodInfo method, TransactionalAttribute attr) =>
        _logger.LogDebug(
            "Transaction BEGIN  — {Method} (isolation={Isolation}, propagation={Propagation})",
            method.Name, attr.IsolationLevel, attr.Propagation);

    /// <inheritdoc/>
    public void OnCommit(MethodInfo method, TimeSpan elapsed) =>
        _logger.LogDebug(
            "Transaction COMMIT — {Method} ({Ms} ms)",
            method.Name, (long)elapsed.TotalMilliseconds);

    /// <inheritdoc/>
    public void OnRollback(MethodInfo method, Exception exception, TimeSpan elapsed) =>
        _logger.LogWarning(exception,
            "Transaction ROLLBACK — {Method} ({Ms} ms) [{ExceptionType}: {ExceptionMessage}]",
            method.Name, (long)elapsed.TotalMilliseconds,
            exception.GetType().Name, exception.Message);
}
