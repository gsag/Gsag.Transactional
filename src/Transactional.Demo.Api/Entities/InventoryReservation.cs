namespace Transactional.Demo.Api.Entities;

public class InventoryReservation
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public string ProductId { get; set; } = "";
    public int Quantity { get; set; }
    public DateTime ReservedAt { get; set; }
}
