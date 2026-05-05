namespace Transactional.Demo.Api.Infrastructure;

public class InMemoryEventBus : IEventBus
{
    private readonly List<string> _events = [];

    public IReadOnlyList<string> Events => _events;

    public void Publish(string eventType, string payload) =>
        _events.Add($"[{DateTime.UtcNow:HH:mm:ss.fff}] {eventType}: {payload}");
}
