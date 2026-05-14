namespace Gsag.Transactional.Demo.Api.Entities;

public class AuditEntry
{
    public int Id { get; set; }
    public required string Action { get; init; }
    public required string Scenario { get; init; }
    public bool Succeeded { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
}
