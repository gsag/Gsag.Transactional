namespace Gsag.Transactional.Demo.Api.Entities;

public class PaymentRecord
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public decimal Amount { get; init; }
    public required string Status { get; init; }
    public DateTimeOffset ProcessedAt { get; init; }
}
