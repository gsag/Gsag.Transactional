using Microsoft.Extensions.Logging;

namespace Gsag.Transactional.Core.Observability;

/// <summary>
/// Default ITransactionObserver that writes structured log entries.
/// Register via AddTransactionalLogging() — do not reference this type directly.
/// </summary>
internal sealed class LoggingTransactionObserver : ITransactionObserver
{
    private readonly ILogger<ITransactionObserver> _logger;

    public LoggingTransactionObserver(ILogger<ITransactionObserver> logger)
        => _logger = logger;

    /// <inheritdoc/>
    public void OnBegin(TransactionInfo info) =>
        _logger.LogDebug(
            "Transaction BEGIN  — {Method} (isolation={Isolation}, propagation={Propagation})",
            info.MethodName, info.IsolationLevel, info.Propagation);

    /// <inheritdoc/>
    public void OnCommit(TransactionInfo info, TimeSpan elapsed) =>
        _logger.LogDebug(
            "Transaction COMMIT — {Method} ({Ms} ms)",
            info.MethodName, (long)elapsed.TotalMilliseconds);

    /// <inheritdoc/>
    public void OnRollback(TransactionInfo info, Exception exception, TimeSpan elapsed) =>
        _logger.LogWarning(exception,
            "Transaction ROLLBACK — {Method} ({Ms} ms) [{ExceptionType}: {ExceptionMessage}]",
            info.MethodName, (long)elapsed.TotalMilliseconds,
            exception.GetType().Name, exception.Message);

    /// <inheritdoc/>
    public void OnComplete(TransactionInfo info, bool committed, TimeSpan elapsed) =>
        _logger.LogDebug(
            "Transaction COMPLETE — {Method} ({Ms} ms) committed={Committed}",
            info.MethodName, (long)elapsed.TotalMilliseconds, committed);
}
