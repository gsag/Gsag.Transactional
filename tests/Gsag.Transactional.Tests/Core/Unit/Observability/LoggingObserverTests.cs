using System.Transactions;
using Gsag.Transactional.Core.Observability;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Gsag.Transactional.Tests.Core.Unit.Observability;

public class LoggingObserverTests
{
    private sealed class FakeLogger : ILogger<LoggingTransactionObserver>
    {
        public readonly List<(LogLevel Level, string Message, Exception? Exception)> Entries = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null!;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception), exception));
    }

    private static readonly TransactionInfo _info = new TransactionInfo
    {
        MethodName = "Stub",
        DeclaringType = typeof(LoggingObserverTests),
        IsolationLevel = IsolationLevel.ReadCommitted,
        Propagation = TransactionScopeOption.Required,
    };

    [Fact]
    public void OnBegin_LogsAtDebug_WithMethodNameAndIsolationLevel()
    {
        var logger = new FakeLogger();
        var observer = new LoggingTransactionObserver(logger);

        observer.OnBegin(_info);

        var (level, message, _) = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Debug, level);
        Assert.Contains("BEGIN", message);
        Assert.Contains(_info.MethodName, message);
    }

    [Fact]
    public void OnCommit_LogsAtDebug_WithMethodNameAndElapsed()
    {
        var logger = new FakeLogger();
        var observer = new LoggingTransactionObserver(logger);

        observer.OnCommit(_info, TimeSpan.FromMilliseconds(42));

        var (level, message, _) = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Debug, level);
        Assert.Contains("COMMIT", message);
        Assert.Contains(_info.MethodName, message);
    }

    [Fact]
    public void OnRollback_LogsAtWarning_WithMethodNameAndException()
    {
        var logger = new FakeLogger();
        var observer = new LoggingTransactionObserver(logger);
        var ex = new InvalidOperationException("boom");

        observer.OnRollback(_info, ex, TimeSpan.FromMilliseconds(10));

        var (level, message, capturedException) = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains("ROLLBACK", message);
        Assert.Contains(_info.MethodName, message);
        Assert.Same(ex, capturedException);
    }

    [Fact]
    public void OnComplete_LogsAtDebug_WithMethodNameAndCommitted()
    {
        var logger = new FakeLogger();
        var observer = new LoggingTransactionObserver(logger);

        observer.OnComplete(_info, committed: true, TimeSpan.FromMilliseconds(55));

        var (level, message, _) = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Debug, level);
        Assert.Contains("COMPLETE", message);
        Assert.Contains(_info.MethodName, message);
    }

    [Fact]
    public void OnComplete_WhenRolledBack_LogsAtDebug_WithCommittedFalse()
    {
        var logger = new FakeLogger();
        var observer = new LoggingTransactionObserver(logger);

        observer.OnComplete(_info, committed: false, TimeSpan.FromMilliseconds(10));

        var (level, message, _) = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Debug, level);
        Assert.Contains("COMPLETE", message);
        Assert.Contains("False", message);
    }
}
