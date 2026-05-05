namespace Transactional.Demo.Api.Infrastructure;

public class HookOutputCollector
{
    private readonly List<string> _events = [];

    public IReadOnlyList<string> Events => _events;

    public void Record(string message) =>
        _events.Add($"[{DateTime.UtcNow:HH:mm:ss.fff}] {message}");
}
