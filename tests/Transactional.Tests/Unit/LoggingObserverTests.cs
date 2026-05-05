using System.Reflection;
using Microsoft.Extensions.Logging;
using Transactional.Core.Attributes;
using Transactional.Core.Observability;
using Xunit;

namespace Transactional.Tests.Unit;

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

    private static readonly MethodInfo _method =
        typeof(LoggingObserverTests).GetMethod(nameof(Stub),
            BindingFlags.NonPublic | BindingFlags.Static)!;

    private static void Stub() { }

    [Fact]
    public void OnBegin_LogsAtDebug_WithMethodNameAndIsolationLevel()
    {
        var logger = new FakeLogger();
        var observer = new LoggingTransactionObserver(logger);

        observer.OnBegin(_method, new TransactionalAttribute());

        var (level, message, _) = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Debug, level);
        Assert.Contains("BEGIN", message);
        Assert.Contains(_method.Name, message);
    }

    [Fact]
    public void OnCommit_LogsAtDebug_WithMethodNameAndElapsed()
    {
        var logger = new FakeLogger();
        var observer = new LoggingTransactionObserver(logger);

        observer.OnCommit(_method, TimeSpan.FromMilliseconds(42));

        var (level, message, _) = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Debug, level);
        Assert.Contains("COMMIT", message);
        Assert.Contains(_method.Name, message);
    }

    [Fact]
    public void OnRollback_LogsAtWarning_WithMethodNameAndException()
    {
        var logger = new FakeLogger();
        var observer = new LoggingTransactionObserver(logger);
        var ex = new InvalidOperationException("boom");

        observer.OnRollback(_method, ex, TimeSpan.FromMilliseconds(10));

        var (level, message, capturedException) = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains("ROLLBACK", message);
        Assert.Contains(_method.Name, message);
        Assert.Same(ex, capturedException);
    }
}
