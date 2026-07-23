using System.Collections.Concurrent;
using System.Reflection;
using Gsag.Transactional.Core.Attributes;
using Gsag.Transactional.Core.Extensions;
using Gsag.Transactional.Core.Hooks;
using Gsag.Transactional.Core.Observability;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gsag.Transactional.Tests.Core.Unit.Proxy;

internal static class AsyncHookTrace
{
    private static readonly ConcurrentQueue<string> _events = new();

    public static void Clear()
    {
        while (_events.TryDequeue(out _))
        {
        }
    }

    public static void Add(string value) => _events.Enqueue(value);

    public static string[] Snapshot() => _events.ToArray();
}

public interface IAsyncHookIntegrationService
{
    [Transactional]
    Task CommitWithBeforeCommitHookAsync();

    [Transactional]
    Task RollbackWithBeforeRollbackHookAsync();

    [Transactional(NoRollbackFor = [typeof(InvalidOperationException)])]
    Task NoRollbackForWithThrowingBeforeCommitHookAsync();

    [Transactional]
    Task CommitWithThrowingBeforeCommitHookAsync();
}

public class AsyncHookIntegrationService : IAsyncHookIntegrationService
{
    private readonly ITransactionHooks _hooks;

    public AsyncHookIntegrationService(ITransactionHooks hooks)
    {
        _hooks = hooks;
    }

    public async Task CommitWithBeforeCommitHookAsync()
    {
        _hooks.BeforeCommit(() => AsyncHookTrace.Add("before-commit"));

        await Task.Yield();
        AsyncHookTrace.Add("body");
    }

    public async Task RollbackWithBeforeRollbackHookAsync()
    {
        _hooks.BeforeRollback(() => AsyncHookTrace.Add("before-rollback"));

        await Task.Yield();
        AsyncHookTrace.Add("body");
        throw new InvalidOperationException("rollback-boom");
    }

    public async Task NoRollbackForWithThrowingBeforeCommitHookAsync()
    {
        _hooks.BeforeCommit(() => AsyncHookTrace.Add("before-commit"));
        _hooks.BeforeCommit(() => throw new InvalidOperationException("hook-boom"));

        await Task.Yield();
        AsyncHookTrace.Add("body");
        throw new InvalidOperationException("method-boom");
    }

    public async Task CommitWithThrowingBeforeCommitHookAsync()
    {
        _hooks.BeforeCommit(() => AsyncHookTrace.Add("before-commit"));
        _hooks.BeforeCommit(() => throw new InvalidOperationException("hook-boom"));

        await Task.Yield();
        AsyncHookTrace.Add("body");
    }
}

public class AsyncHookIntegrationTests
{
    private static ServiceProvider CreateProvider(RecordingObserver observer)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITransactionObserver>(observer);
        services.AddTransactional(b => b.ScanAssembly(Assembly.GetExecutingAssembly()));
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task CommitPath_ExecutesBeforeCommitHookAfterBodyAndCommits()
    {
        AsyncHookTrace.Clear();
        var observer = new RecordingObserver();

        using var provider = CreateProvider(observer);
        using var scope = provider.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IAsyncHookIntegrationService>();

        await svc.CommitWithBeforeCommitHookAsync();

        Assert.Equal(["body", "before-commit"], AsyncHookTrace.Snapshot());
        Assert.Contains("COMMIT:CommitWithBeforeCommitHookAsync", observer.Calls);
        Assert.Contains("COMPLETE:CommitWithBeforeCommitHookAsync:True", observer.Calls);
    }

    [Fact]
    public async Task CommitPath_WhenBeforeCommitHookThrows_RollsBackAndPropagatesHookException()
    {
        AsyncHookTrace.Clear();
        var observer = new RecordingObserver();

        using var provider = CreateProvider(observer);
        using var scope = provider.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IAsyncHookIntegrationService>();

        var ex = await Assert.ThrowsAsync<AggregateException>(() => svc.CommitWithThrowingBeforeCommitHookAsync());

        Assert.Contains("hook-boom", ex.InnerExceptions[0].Message);
        Assert.Equal(["body", "before-commit"], AsyncHookTrace.Snapshot());
        Assert.Contains("ROLLBACK:CommitWithThrowingBeforeCommitHookAsync", observer.Calls);
        Assert.Contains("COMPLETE:CommitWithThrowingBeforeCommitHookAsync:False", observer.Calls);
        Assert.DoesNotContain("COMMIT:CommitWithThrowingBeforeCommitHookAsync", observer.Calls);
    }

    [Fact]
    public async Task RollbackPath_ExecutesBeforeRollbackHookAfterBodyAndRollsBack()
    {
        AsyncHookTrace.Clear();
        var observer = new RecordingObserver();

        using var provider = CreateProvider(observer);
        using var scope = provider.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IAsyncHookIntegrationService>();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RollbackWithBeforeRollbackHookAsync());

        Assert.Equal("rollback-boom", ex.Message);
        Assert.Equal(["body", "before-rollback"], AsyncHookTrace.Snapshot());
        Assert.Contains("ROLLBACK:RollbackWithBeforeRollbackHookAsync", observer.Calls);
        Assert.Contains("COMPLETE:RollbackWithBeforeRollbackHookAsync:False", observer.Calls);
    }

    [Fact]
    public async Task NoRollbackForPath_CommitsEvenWhenBeforeCommitHookThrows()
    {
        AsyncHookTrace.Clear();
        var observer = new RecordingObserver();

        using var provider = CreateProvider(observer);
        using var scope = provider.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IAsyncHookIntegrationService>();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.NoRollbackForWithThrowingBeforeCommitHookAsync());

        Assert.Equal("method-boom", ex.Message);
        Assert.Equal(["body", "before-commit"], AsyncHookTrace.Snapshot());
        Assert.Contains("COMMIT:NoRollbackForWithThrowingBeforeCommitHookAsync", observer.Calls);
        Assert.Contains("COMPLETE:NoRollbackForWithThrowingBeforeCommitHookAsync:True", observer.Calls);
        Assert.DoesNotContain("ROLLBACK:NoRollbackForWithThrowingBeforeCommitHookAsync", observer.Calls);
    }
}