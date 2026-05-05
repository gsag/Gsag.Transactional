namespace Transactional.Demo.Api.Entities;

public class AuditEntry
{
    public int Id { get; set; }
    public string Action { get; set; } = "";
    public string Scenario { get; set; } = "";
    public bool Succeeded { get; set; }
    public DateTime OccurredAt { get; set; }
}
