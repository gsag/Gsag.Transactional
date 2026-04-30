using Microsoft.AspNetCore.Mvc;
using Transactional.Demo.Api.Services;

namespace Transactional.Demo.Api.Controllers;

[ApiController]
[Route("fulfillment")]
public class OrderFulfillmentController : ControllerBase
{
    private readonly IOrderFulfillmentService _fulfillment;

    public OrderFulfillmentController(IOrderFulfillmentService fulfillment)
        => _fulfillment = fulfillment;

    /// <summary>
    /// Outer [Transactional] (Required) wraps a call to CreateRequiresNewAsync (RequiresNew).
    /// Both scopes commit — order is persisted.
    /// </summary>
    [HttpPost("fulfill")]
    public async Task<IActionResult> Fulfill()
    {
        var order = await _fulfillment.FulfillAsync();
        return Created($"/orders/{order.Id}", order);
    }

    /// <summary>
    /// Outer [Transactional] (Required) calls CreateRequiresNewAsync (inner RequiresNew commits),
    /// then throws. Demonstrates that the inner scope commits independently before the outer fails.
    /// </summary>
    [HttpPost("fulfill-then-fail")]
    public async Task<IActionResult> FulfillThenFail()
    {
        try
        {
            await _fulfillment.FulfillThenFailAsync();
            return Ok();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message, note = "Inner RequiresNew committed — check GET /orders." });
        }
    }
}
