namespace Transactional.Demo.Api.Entities;

public class CheckoutOrder
{
    public int Id { get; set; }
    public string Scenario { get; set; } = "";
    public string Status { get; set; } = "created";
    public decimal Amount { get; set; }
    public DateTime CreatedAt { get; set; }
}
