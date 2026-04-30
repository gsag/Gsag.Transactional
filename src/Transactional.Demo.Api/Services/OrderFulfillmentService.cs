using Transactional.Core.Attributes;
using Transactional.Demo.Api.Entities;

namespace Transactional.Demo.Api.Services;

public class OrderFulfillmentService : IOrderFulfillmentService
{
    private readonly IOrderService _orders;

    public OrderFulfillmentService(IOrderService orders) => _orders = orders;

    [Transactional]
    public async Task<Order> FulfillAsync()
    {
        return await _orders.CreateRequiresNewAsync();
    }

    [Transactional]
    public async Task FulfillThenFailAsync()
    {
        await _orders.CreateRequiresNewAsync();
        throw new InvalidOperationException(
            "Outer Required transaction failed after inner RequiresNew already committed.");
    }
}
