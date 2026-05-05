using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Transactional.Demo.Api.Data;
using Transactional.Demo.Api.Exceptions;
using Transactional.Demo.Api.Infrastructure;
using Transactional.Demo.Api.Services;

namespace Transactional.Demo.Api.Controllers;

public record CheckoutResponse(
    string Scenario,
    string Outcome,
    string TransactionNote,
    object? Data,
    IReadOnlyList<string> HooksOutput,
    IReadOnlyList<string> PublishedEvents,
    string? Error = null);

[ApiController]
[Route("checkout")]
public class CheckoutController : ControllerBase
{
    private readonly ICheckoutService _checkout;
    private readonly IOrderService _orderService;
    private readonly IAuditService _auditService;
    private readonly IPaymentService _paymentService;
    private readonly HookOutputCollector _collector;
    private readonly IEventBus _eventBus;
    private readonly CheckoutDbContext _db;

    public CheckoutController(
        ICheckoutService checkout,
        IOrderService orderService,
        IAuditService auditService,
        IPaymentService paymentService,
        HookOutputCollector collector,
        IEventBus eventBus,
        CheckoutDbContext db)
    {
        _checkout = checkout;
        _orderService = orderService;
        _auditService = auditService;
        _paymentService = paymentService;
        _collector = collector;
        _eventBus = eventBus;
        _db = db;
    }

    /// <summary>
    /// Full checkout — all services commit.
    /// Demonstrates: Required scope joining, RequiresNew audit, AfterCommit hooks from inner services.
    /// </summary>
    [HttpPost("success")]
    public async Task<ActionResult<CheckoutResponse>> Success(CancellationToken ct)
    {
        var result = await _checkout.ProcessSuccessAsync(ct);
        return Created("", Build("success", "committed",
            "OrderService, InventoryService and PaymentService each have [Transactional(Required)] and joined the outer CheckoutService scope. " +
            "AuditService opened an independent [Transactional(RequiresNew)] scope and committed first. " +
            "AfterCommit hooks registered inside inner services fired after the OUTER scope called Complete() — not when each inner method returned. " +
            "See HooksOutput for execution order.",
            data: new { result.Order, result.Reservation, result.Payment, result.Audit }));
    }

    /// <summary>
    /// Card declined — outer scope rolls back.
    /// Demonstrates: rollback lifecycle, AfterRollback hooks, observer OnRollback event.
    /// </summary>
    [HttpPost("payment-failure")]
    public async Task<ActionResult<CheckoutResponse>> PaymentFailure(CancellationToken ct)
    {
        try
        {
            await _checkout.ProcessWithPaymentFailureAsync(ct);
            return Ok();
        }
        catch (PaymentDeclinedException ex)
        {
            return Problem(
                title: "Payment declined",
                detail: ex.Message,
                statusCode: 400,
                extensions: new Dictionary<string, object?>
                {
                    ["scenario"] = "payment-failure",
                    ["outcome"] = "rolled_back",
                    ["transactionNote"] = "PaymentService.FailCardDeclinedAsync threw PaymentDeclinedException before any SaveChanges. The outer Required scope was disposed without Complete() — observer received OnRollback. AfterRollback hooks ran. No data was written to the database.",
                    ["hooksOutput"] = _collector.Events,
                    ["publishedEvents"] = _eventBus.Events
                });
        }
    }

    /// <summary>
    /// Out of stock — outer scope rolls back before any write.
    /// Demonstrates: rollback from an inner Required service, identical lifecycle to payment failure.
    /// </summary>
    [HttpPost("inventory-failure")]
    public async Task<ActionResult<CheckoutResponse>> InventoryFailure(CancellationToken ct)
    {
        try
        {
            await _checkout.ProcessWithInventoryFailureAsync(ct);
            return Ok();
        }
        catch (InventoryException ex)
        {
            return Problem(
                title: "Inventory unavailable",
                detail: ex.Message,
                statusCode: 400,
                extensions: new Dictionary<string, object?>
                {
                    ["scenario"] = "inventory-failure",
                    ["outcome"] = "rolled_back",
                    ["transactionNote"] = "InventoryService.FailOutOfStockAsync threw InventoryException before SaveChanges. The outer scope disposed without Complete(). No inventory reservation or order was persisted.",
                    ["hooksOutput"] = _collector.Events,
                    ["publishedEvents"] = _eventBus.Events
                });
        }
    }

    /// <summary>
    /// RequiresNew audit survives outer rollback.
    /// Demonstrates: AuditEntry persists even though the checkout transaction failed.
    /// Verify: GET /checkout/audit-log shows the entry despite the 400 response here.
    /// </summary>
    [HttpPost("audit-requires-new")]
    public async Task<ActionResult<CheckoutResponse>> AuditRequiresNew(CancellationToken ct)
    {
        try
        {
            await _checkout.ProcessWithAuditRequiresNewAsync(ct);
            return Ok();
        }
        catch (InvalidOperationException ex)
        {
            return Problem(
                title: "Checkout failed — audit survived",
                detail: ex.Message,
                statusCode: 400,
                extensions: new Dictionary<string, object?>
                {
                    ["scenario"] = "audit-requires-new",
                    ["outcome"] = "rolled_back",
                    ["transactionNote"] = "AuditService.WriteAsync used [Transactional(RequiresNew)] — opened an independent scope and committed before the outer scope threw. " +
                                          "The outer Required scope then rolled back, but the AuditEntry is already persisted. " +
                                          "Verify: GET /checkout/audit-log should contain the entry despite this 400 response.",
                    ["hooksOutput"] = _collector.Events,
                    ["publishedEvents"] = _eventBus.Events
                });
        }
    }

    /// <summary>
    /// NotificationException commits instead of rolling back.
    /// Demonstrates: [Transactional(NoRollbackFor)] — scope.Complete() called despite exception.
    /// Verify: GET /checkout/orders and GET /checkout/payments show records even though this returns 400.
    /// </summary>
    [HttpPost("no-rollback-for")]
    public async Task<ActionResult<CheckoutResponse>> NoRollbackFor(CancellationToken ct)
    {
        try
        {
            await _checkout.ProcessWithNoRollbackForAsync(ct);
            return Ok();
        }
        catch (NotificationException ex)
        {
            return Problem(
                title: "Notification failed — data committed",
                detail: ex.Message,
                statusCode: 400,
                extensions: new Dictionary<string, object?>
                {
                    ["scenario"] = "no-rollback-for",
                    ["outcome"] = "committed",
                    ["transactionNote"] = "CheckoutService was annotated with [Transactional(NoRollbackFor = [typeof(NotificationException)])]. " +
                                          "When NotificationException was thrown, the proxy called scope.Complete() instead of disposing without it. " +
                                          "Order and payment records were saved before the throw — both persist. " +
                                          "Note: outcome is 'committed' even though this response is HTTP 400.",
                    ["hooksOutput"] = _collector.Events,
                    ["publishedEvents"] = _eventBus.Events
                });
        }
    }

    /// <summary>
    /// AfterCommit hook — event published only after scope.Complete().
    /// Demonstrates: hooks registered in inner services fire at outer scope commit, not at method return.
    /// Check PublishedEvents in the response — it contains the payment.approved event.
    /// </summary>
    [HttpPost("after-commit-hook")]
    public async Task<ActionResult<CheckoutResponse>> AfterCommitHook(CancellationToken ct)
    {
        var result = await _checkout.ProcessWithAfterCommitHookAsync(ct);
        return Created("", Build("after-commit-hook", "committed",
            "PaymentService.ProcessAsync registered an AfterCommit hook to publish 'payment.approved'. " +
            "The hook did NOT fire when ProcessAsync returned — it fired after the outer CheckoutService scope called Complete(). " +
            "This guarantees events reach the bus only after data is durably committed. " +
            "Check PublishedEvents in this response.",
            data: new { result.Order, result.Payment }));
    }

    /// <summary>
    /// AfterRollback hooks — compensating actions on failure.
    /// Demonstrates: multiple hooks (sync + async), all execute in registration order.
    /// Check HooksOutput for the three compensating actions that ran after rollback.
    /// </summary>
    [HttpPost("after-rollback-hook")]
    public async Task<ActionResult<CheckoutResponse>> AfterRollbackHook(CancellationToken ct)
    {
        try
        {
            await _checkout.ProcessWithAfterRollbackHookAsync(ct);
            return Ok();
        }
        catch (InvalidOperationException ex)
        {
            return Problem(
                title: "Checkout failed — compensating hooks ran",
                detail: ex.Message,
                statusCode: 400,
                extensions: new Dictionary<string, object?>
                {
                    ["scenario"] = "after-rollback-hook",
                    ["outcome"] = "rolled_back",
                    ["transactionNote"] = "Three AfterRollback hooks were registered (sync, async, sync) plus one AfterCompletion. " +
                                          "All hooks ran in registration order after scope.Dispose() without Complete(). " +
                                          "Each hook represents a compensating action: releasing stock, alerting ops, updating status. " +
                                          "Check HooksOutput to see all hooks executed in sequence.",
                    ["hooksOutput"] = _collector.Events,
                    ["publishedEvents"] = _eventBus.Events
                });
        }
    }

    /// <summary>
    /// Suppress — inventory report runs outside the ambient transaction.
    /// Demonstrates: [Transactional(Suppress)] suspends the outer scope; Transaction.Current is null inside.
    /// Check HooksOutput — InventoryReportService confirms Transaction.Current = null.
    /// </summary>
    [HttpPost("suppress")]
    public async Task<ActionResult<CheckoutResponse>> SuppressRead(CancellationToken ct)
    {
        var result = await _checkout.ProcessWithSuppressAsync(ct);
        return Created("", Build("suppress", "committed",
            "CheckoutService opened a Required scope. Inside it, InventoryReportService.ReadAvailableStockAsync " +
            "used [Transactional(Suppress)] to suspend the ambient transaction for its duration. " +
            "Transaction.Current was null inside the Suppress scope — confirmed in HooksOutput. " +
            "After ReadAvailableStockAsync returned, the outer Required scope was automatically resumed.",
            data: new { result.Order, result.Payment, result.StockReport }));
    }

    /// <summary>Returns all persisted checkout orders.</summary>
    [HttpGet("orders")]
    public async Task<IActionResult> GetOrders(CancellationToken ct)
    {
        var orders = await _orderService.GetAllAsync(ct);
        return Ok(orders);
    }

    /// <summary>Returns all persisted audit log entries.</summary>
    [HttpGet("audit-log")]
    public async Task<IActionResult> GetAuditLog(CancellationToken ct)
    {
        var entries = await _auditService.GetAllAsync(ct);
        return Ok(entries);
    }

    /// <summary>Returns all persisted payment records.</summary>
    [HttpGet("payments")]
    public async Task<IActionResult> GetPayments(CancellationToken ct)
    {
        var payments = await _paymentService.GetAllAsync(ct);
        return Ok(payments);
    }

    /// <summary>Clears all data. Use between demo runs to start with a clean state.</summary>
    [HttpDelete("reset")]
    public async Task<IActionResult> Reset(CancellationToken ct)
    {
        await _db.Payments.ExecuteDeleteAsync(ct);
        await _db.Reservations.ExecuteDeleteAsync(ct);
        await _db.AuditEntries.ExecuteDeleteAsync(ct);
        await _db.Orders.ExecuteDeleteAsync(ct);
        return Ok(new { message = "All data cleared. Ready for next demo run." });
    }

    private CheckoutResponse Build(string scenario, string outcome, string transactionNote, object? data = null, string? error = null) =>
        new(scenario, outcome, transactionNote, data, _collector.Events, _eventBus.Events, error);
}
