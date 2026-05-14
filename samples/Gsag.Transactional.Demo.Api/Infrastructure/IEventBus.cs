namespace Gsag.Transactional.Demo.Api.Infrastructure;

public interface IEventBus
{
    void Publish(string eventType, string payload);
    IReadOnlyList<string> Events { get; }
}
