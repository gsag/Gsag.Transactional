namespace Transactional.Demo.Api.Entities;

public class InventoryReservation
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public required string ProductId { get; init; }
    public int Quantity { get; init; }
    public DateTimeOffset ReservedAt { get; init; }
}
