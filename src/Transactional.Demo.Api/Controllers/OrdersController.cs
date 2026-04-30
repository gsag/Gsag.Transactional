using Microsoft.AspNetCore.Mvc;
using Transactional.Demo.Api.Services;

namespace Transactional.Demo.Api.Controllers;

[ApiController]
[Route("orders")]
public class OrdersController : ControllerBase
{
    private readonly IOrderService _orderService;

    public OrdersController(IOrderService orderService) => _orderService = orderService;

    /// <summary>Creates an order. Transaction commits successfully.</summary>
    [HttpPost("success")]
    public async Task<IActionResult> CreateSuccess()
    {
        var order = await _orderService.CreateSuccessAsync();
        return Created($"/orders/{order.Id}", order);
    }

    /// <summary>
    /// Tries to create an order but throws after writing.
    /// Transaction is rolled back — no order persisted.
    /// </summary>
    [HttpPost("fail")]
    public async Task<IActionResult> CreateWithFail()
    {
        try
        {
            await _orderService.CreateWithRollbackAsync();
            return Ok();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message, note = "Transaction was rolled back — no order was saved." });
        }
    }

    /// <summary>Returns all persisted orders.</summary>
    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var orders = await _orderService.GetAllAsync();
        return Ok(orders);
    }
}
