using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Transactional.Demo.Api.Data;
using Transactional.Demo.Api.Entities;
using Transactional.Demo.Api.Exceptions;
using Transactional.Demo.Api.Infrastructure;
using Transactional.Demo.Api.Services;

namespace Transactional.Demo.Api.Controllers;

/// <summary>Response envelope returned by all scenario POST endpoints.</summary>
public record CheckoutResponse(
    /// <summary>Scenario identifier matching the endpoint path segment.</summary>
    string Scenario,
    /// <summary><c>committed</c> or <c>rolled_back</c>.</summary>
    string Outcome,
    /// <summary>Human-readable explanation of the transaction lifecycle exercised by this scenario.</summary>
    string TransactionNote,
    /// <summary>Entities created during this scenario, or <c>null</c> on pure-rollback paths.</summary>
    object? Data,
    /// <summary>
    /// Timestamped log of every hook registration and execution in this request.
    /// Reveals the ordering between inner-service hooks and outer-scope commit/rollback.
    /// </summary>
    IReadOnlyList<string> HooksOutput,
    /// <summary>Events published to the in-memory event bus during this request. Only populated on commit paths.</summary>
    IReadOnlyList<string> PublishedEvents,
    /// <summary>Exception message on rollback paths; <c>null</c> on committed paths.</summary>
    string? Error = null);

/// <summary>Response body returned by DELETE /checkout/reset.</summary>
public record ResetResponse(
    /// <summary>Human-readable confirmation that all tables were cleared.</summary>
    string Message);

/// <summary>Cumulative transaction counters collected by <see cref="InMemoryMetricsObserver"/>.</summary>
public record MetricsResponse(
    /// <summary>Total number of transaction scopes opened since startup.</summary>
    long TotalTransactions,
    /// <summary>Number of transactions that committed successfully.</summary>
    long Committed,
    /// <summary>Number of transactions that rolled back.</summary>
    long RolledBack,
    /// <summary>Total execution time across all completed transactions, in milliseconds.</summary>
    long TotalElapsedMs,
    /// <summary>Average execution time per completed transaction, in milliseconds. Zero when no transactions have completed.</summary>
    double AvgElapsedMs);

/// <summary>
/// Demonstrates eight distinct behaviours of the <c>[Transactional]</c> attribute library,
/// each isolated in its own endpoint. Every POST response includes <c>HooksOutput</c> and
/// <c>PublishedEvents</c> so the transaction lifecycle is directly observable in the body.
/// </summary>
[ApiController]
[Route("checkout")]
[Produces("application/json")]
public class CheckoutController : ControllerBase
{
    private readonly ICheckoutService _checkout;
    private readonly IOrderService _orderService;
    private readonly IAuditService _auditService;
    private readonly IPaymentService _paymentService;
    private readonly HookOutputCollector _collector;
    private readonly IEventBus _eventBus;
    private readonly CheckoutDbContext _db;
    private readonly InMemoryMetricsObserver _metrics;

    public CheckoutController(
        ICheckoutService checkout,
        IOrderService orderService,
        IAuditService auditService,
        IPaymentService paymentService,
        HookOutputCollector collector,
        IEventBus eventBus,
        CheckoutDbContext db,
        InMemoryMetricsObserver metrics)
    {
        _checkout = checkout;
        _orderService = orderService;
        _auditService = auditService;
        _paymentService = paymentService;
        _collector = collector;
        _eventBus = eventBus;
        _db = db;
        _metrics = metrics;
    }

    // -------------------------------------------------------------------------
    // Scenario endpoints
    // -------------------------------------------------------------------------

    /// <summary>Full checkout — all services commit.</summary>
    /// <remarks>
    /// <b>Propagation:</b> <c>Required</c> on all inner services — they join the outer scope.<br/>
    /// <b>Hooks:</b> AfterCommit hooks registered in OrderService, InventoryService and PaymentService
    /// fire when the <em>outer</em> CheckoutService scope calls <c>Complete()</c>, not when each inner
    /// method returns. AuditService uses <c>RequiresNew</c> and commits independently before the outer scope closes.<br/>
    /// Inspect <c>HooksOutput</c> in the response to see the exact execution order.
    /// </remarks>
    /// <response code="201">All entities committed. Response includes Order, Reservation, Payment and Audit.</response>
    [HttpPost("success")]
    [ProducesResponseType(typeof(CheckoutResponse), StatusCodes.Status201Created)]
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

    /// <summary>Card declined — outer scope rolls back.</summary>
    /// <remarks>
    /// <b>Propagation:</b> <c>Required</c> — PaymentService joins the outer scope.<br/>
    /// PaymentService throws <c>PaymentDeclinedException</c> <em>before</em> any <c>SaveChanges</c>,
    /// so no data reaches the database. The outer scope disposes without <c>Complete()</c>.
    /// AfterRollback hooks fire and are visible in the extended ProblemDetails response fields
    /// <c>hooksOutput</c> and <c>publishedEvents</c>.
    /// </remarks>
    /// <response code="400">
    /// Transaction rolled back. No data persisted.
    /// Extended ProblemDetails fields: <c>scenario</c>, <c>outcome</c>, <c>transactionNote</c>,
    /// <c>hooksOutput</c>, <c>publishedEvents</c>.
    /// </response>
    [HttpPost("payment-failure")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
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

    /// <summary>Out of stock — outer scope rolls back before any write.</summary>
    /// <remarks>
    /// <b>Propagation:</b> <c>Required</c> — InventoryService joins the outer scope.<br/>
    /// InventoryService throws <c>InventoryException</c> before any <c>SaveChanges</c>.
    /// Identical rollback lifecycle to <c>payment-failure</c> — demonstrates the pattern is
    /// service-agnostic.
    /// </remarks>
    /// <response code="400">
    /// Transaction rolled back. No data persisted.
    /// Extended ProblemDetails fields: <c>scenario</c>, <c>outcome</c>, <c>transactionNote</c>,
    /// <c>hooksOutput</c>, <c>publishedEvents</c>.
    /// </response>
    [HttpPost("inventory-failure")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
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

    /// <summary>RequiresNew audit survives outer rollback.</summary>
    /// <remarks>
    /// <b>Propagation:</b> <c>Required</c> outer + <c>RequiresNew</c> on AuditService.<br/>
    /// AuditService opens an independent scope, commits the AuditEntry, then returns.
    /// The outer scope subsequently throws and rolls back — the AuditEntry is already durable.<br/>
    /// Verify by calling <c>GET /checkout/audit-log</c> after this 400 response.
    /// On SQL Server / PostgreSQL the outer-scope order write would also be rolled back;
    /// on SQLite both persist because SQLite does not support ambient transaction enlistment.
    /// </remarks>
    /// <response code="400">
    /// Outer scope rolled back. AuditEntry persists independently (RequiresNew).
    /// Extended ProblemDetails fields: <c>scenario</c>, <c>outcome</c>, <c>transactionNote</c>,
    /// <c>hooksOutput</c>, <c>publishedEvents</c>.
    /// </response>
    [HttpPost("audit-requires-new")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
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

    /// <summary>NotificationException commits instead of rolling back.</summary>
    /// <remarks>
    /// <b>Propagation:</b> <c>Required</c> with <c>NoRollbackFor = [typeof(NotificationException)]</c>.<br/>
    /// When <c>NotificationException</c> propagates out of the method, the proxy calls
    /// <c>scope.Complete()</c> instead of disposing without it — the scope commits even though
    /// an exception was thrown. Order and payment records persist despite the HTTP 400 response.<br/>
    /// Verify by calling <c>GET /checkout/orders</c> and <c>GET /checkout/payments</c> after this response.
    /// </remarks>
    /// <response code="400">
    /// HTTP 400 is returned but the transaction was <b>committed</b> (NoRollbackFor in effect).
    /// Order and payment records are in the database.
    /// Extended ProblemDetails fields: <c>scenario</c>, <c>outcome</c> (<c>committed</c>),
    /// <c>transactionNote</c>, <c>hooksOutput</c>, <c>publishedEvents</c>.
    /// </response>
    [HttpPost("no-rollback-for")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
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

    /// <summary>AfterCommit hook — event published only after scope.Complete().</summary>
    /// <remarks>
    /// <b>Propagation:</b> <c>Required</c>.<br/>
    /// PaymentService registers an <c>AfterCommit</c> hook inside its <c>[Transactional]</c> method.
    /// The hook does <em>not</em> fire when <c>ProcessAsync</c> returns to the caller — it fires only
    /// after the outer CheckoutService scope calls <c>Complete()</c>.
    /// This guarantees events are published only after all data is durably committed.<br/>
    /// Inspect <c>PublishedEvents</c> in the response for the <c>payment.approved</c> event.
    /// </remarks>
    /// <response code="201">Order and payment committed. PublishedEvents contains the payment.approved event.</response>
    [HttpPost("after-commit-hook")]
    [ProducesResponseType(typeof(CheckoutResponse), StatusCodes.Status201Created)]
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

    /// <summary>AfterRollback hooks — compensating actions on failure.</summary>
    /// <remarks>
    /// <b>Propagation:</b> <c>Required</c>.<br/>
    /// Three hooks are registered before the simulated failure: two synchronous and one asynchronous.
    /// All execute in registration order after the scope disposes without <c>Complete()</c>.
    /// Use this pattern for compensating actions: releasing allocations, alerting on-call, updating status.<br/>
    /// Inspect <c>HooksOutput</c> in the extended response fields to see all three hooks plus AfterCompletion.
    /// </remarks>
    /// <response code="400">
    /// Transaction rolled back. Three compensating AfterRollback hooks executed in order.
    /// Extended ProblemDetails fields: <c>scenario</c>, <c>outcome</c>, <c>transactionNote</c>,
    /// <c>hooksOutput</c>, <c>publishedEvents</c>.
    /// </response>
    [HttpPost("after-rollback-hook")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
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

    /// <summary>Suppress — inventory report runs outside the ambient transaction.</summary>
    /// <remarks>
    /// <b>Propagation:</b> <c>Required</c> outer + <c>Suppress</c> on InventoryReportService.<br/>
    /// While inside the outer Required scope, <c>ReadAvailableStockAsync</c> opens a Suppress scope
    /// that suspends the ambient transaction. <c>Transaction.Current</c> is <c>null</c> inside the
    /// Suppress scope — confirmed in <c>HooksOutput</c>. After the call returns, the outer scope is
    /// automatically resumed and commits normally.
    /// </remarks>
    /// <response code="201">Order and payment committed. HooksOutput confirms Transaction.Current = null inside Suppress scope.</response>
    [HttpPost("suppress")]
    [ProducesResponseType(typeof(CheckoutResponse), StatusCodes.Status201Created)]
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

    // -------------------------------------------------------------------------
    // Read / utility endpoints
    // -------------------------------------------------------------------------

    /// <summary>Returns all persisted checkout orders, newest first.</summary>
    /// <response code="200">List of checkout orders ordered by <c>CreatedAt</c> descending.</response>
    [HttpGet("orders")]
    [ProducesResponseType(typeof(IReadOnlyList<CheckoutOrder>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<CheckoutOrder>>> GetOrders(CancellationToken ct)
    {
        var orders = await _orderService.GetAllAsync(ct);
        return Ok(orders);
    }

    /// <summary>Returns all persisted audit log entries, newest first.</summary>
    /// <response code="200">List of audit entries ordered by <c>OccurredAt</c> descending.</response>
    [HttpGet("audit-log")]
    [ProducesResponseType(typeof(IReadOnlyList<AuditEntry>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<AuditEntry>>> GetAuditLog(CancellationToken ct)
    {
        var entries = await _auditService.GetAllAsync(ct);
        return Ok(entries);
    }

    /// <summary>Returns all persisted payment records, newest first.</summary>
    /// <response code="200">List of payment records ordered by <c>ProcessedAt</c> descending.</response>
    [HttpGet("payments")]
    [ProducesResponseType(typeof(IReadOnlyList<PaymentRecord>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<PaymentRecord>>> GetPayments(CancellationToken ct)
    {
        var payments = await _paymentService.GetAllAsync(ct);
        return Ok(payments);
    }

    /// <summary>Cumulative transaction metrics collected by the Composite Observer.</summary>
    /// <remarks>
    /// Demonstrates the <b>Composite Observer</b> pattern: <c>LoggingTransactionObserver</c> and
    /// <c>InMemoryMetricsObserver</c> are both registered via <c>AddTransactionalObserver&lt;T&gt;()</c>.
    /// The proxy wraps them in a <c>CompositeTransactionObserver</c> and calls each in sequence —
    /// neither observer knows about the other, and no existing class was modified.<br/>
    /// Counters are cumulative from startup. Call <c>DELETE /checkout/reset</c> to clear data
    /// (counters are not reset — they are observer-level, not DB-level).
    /// </remarks>
    /// <response code="200">Cumulative transaction counters since API startup.</response>
    [HttpGet("metrics")]
    [ProducesResponseType(typeof(MetricsResponse), StatusCodes.Status200OK)]
    public ActionResult<MetricsResponse> GetMetrics()
    {
        var completed = _metrics.CompletedCount;
        var avgMs = completed > 0 ? (double)_metrics.TotalElapsedMs / completed : 0;
        return Ok(new MetricsResponse(
            TotalTransactions: _metrics.TotalTransactions,
            Committed:         _metrics.Committed,
            RolledBack:        _metrics.RolledBack,
            TotalElapsedMs:    _metrics.TotalElapsedMs,
            AvgElapsedMs:      Math.Round(avgMs, 2)));
    }

    /// <summary>Clears all data from every table. Use between demo runs to start with a clean state.</summary>
    /// <response code="200">All tables cleared successfully.</response>
    [HttpDelete("reset")]
    [ProducesResponseType(typeof(ResetResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<ResetResponse>> Reset(CancellationToken ct)
    {
        await _db.Payments.ExecuteDeleteAsync(ct);
        await _db.Reservations.ExecuteDeleteAsync(ct);
        await _db.AuditEntries.ExecuteDeleteAsync(ct);
        await _db.Orders.ExecuteDeleteAsync(ct);
        return Ok(new ResetResponse("All data cleared. Ready for next demo run."));
    }

    private CheckoutResponse Build(string scenario, string outcome, string transactionNote, object? data = null, string? error = null) =>
        new(scenario, outcome, transactionNote, data, _collector.Events, _eventBus.Events, error);
}
