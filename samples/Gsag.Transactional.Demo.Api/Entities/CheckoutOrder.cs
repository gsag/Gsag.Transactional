namespace Gsag.Transactional.Demo.Api.Entities;

public class CheckoutOrder
{
    public int Id { get; set; }
    public required string Scenario { get; init; }
    public required string Status { get; init; }
    public decimal Amount { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
