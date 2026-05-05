using System.Collections.Concurrent;

namespace Transactional.Demo.Api.Infrastructure;

public class InMemoryEventBus : IEventBus
{
    // ConcurrentQueue: AfterCommit hooks that publish events may run on thread-pool continuations.
    private readonly ConcurrentQueue<string> _events = new();

    public IReadOnlyList<string> Events => [.. _events];

    public void Publish(string eventType, string payload) =>
        _events.Enqueue($"[{DateTimeOffset.UtcNow:HH:mm:ss.fff}] {eventType}: {payload}");
}
