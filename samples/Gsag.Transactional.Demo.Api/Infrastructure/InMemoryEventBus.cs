using System.Collections.Concurrent;

namespace Gsag.Transactional.Demo.Api.Infrastructure;

public class InMemoryEventBus : IEventBus
{
    // ConcurrentQueue: AfterCommit hooks that publish events may run on thread-pool continuations.
    private readonly ConcurrentQueue<string> _events = new();

    public IReadOnlyList<string> Events => [.. _events];

    public void Publish(string eventType, string payload) =>
        _events.Enqueue($"[{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] {eventType}: {payload}");
}
