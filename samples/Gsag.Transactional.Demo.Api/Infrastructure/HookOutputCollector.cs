using System.Collections.Concurrent;

namespace Gsag.Transactional.Demo.Api.Infrastructure;

public class HookOutputCollector
{
    // ConcurrentQueue because AfterCommit/AfterRollback hooks may execute on a thread-pool
    // continuation different from the request thread. Hooks run sequentially today, but the
    // queue is safe if that ever changes.
    private readonly ConcurrentQueue<string> _events = new();

    public IReadOnlyList<string> Events => [.. _events];

    public void Record(string message) =>
        _events.Enqueue($"[{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] {message}");
}
