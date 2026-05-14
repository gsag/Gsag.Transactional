using Gsag.Transactional.Core.Attributes;
using Gsag.Transactional.Core.Hooks;
using Gsag.Transactional.Demo.Api.Data;
using Gsag.Transactional.Demo.Api.Entities;
using Gsag.Transactional.Demo.Api.Exceptions;
using Gsag.Transactional.Demo.Api.Infrastructure;

namespace Gsag.Transactional.Demo.Api.Services;

public class CheckoutService : ICheckoutService
{
    private readonly IOrderService _orders;
    private readonly IInventoryService _inventory;
    private readonly IPaymentService _payments;
    private readonly IAuditService _audit;
    private readonly IInventoryReportService _inventoryReport;
    private readonly ITransactionHooks _hooks;
    private readonly HookOutputCollector _collector;
    private readonly CheckoutDbContext _db;

    public CheckoutService(
        IOrderService orders,
        IInventoryService inventory,
        IPaymentService payments,
        IAuditService audit,
        IInventoryReportService inventoryReport,
        ITransactionHooks hooks,
        HookOutputCollector collector,
        CheckoutDbContext db)
    {
        _orders = orders;
        _inventory = inventory;
        _payments = payments;
        _audit = audit;
        _inventoryReport = inventoryReport;
        _hooks = hooks;
        _collector = collector;
        _db = db;
    }

    /// <summary>
    /// Scenario 1 — Full success.
    /// CheckoutService opens a Required outer scope.
    /// OrderService, InventoryService, PaymentService each have [Transactional(Required)] and JOIN the outer scope.
    /// AuditService has [Transactional(RequiresNew)] — commits independently in its own scope.
    /// Hooks registered in all inner services fire when the OUTER scope commits (not when each inner method returns).
    /// </summary>
    [Transactional]
    public async Task<CheckoutResult> ProcessSuccessAsync(CancellationToken ct = default)
    {
        _collector.Record("CheckoutService: outer Required scope opened");

        var order = await _orders.CreateAsync("success", 99.99m, ct);
        var reservation = await _inventory.ReserveAsync(order.Id, "PROD-001", 1, ct);
        var payment = await _payments.ProcessAsync(order.Id, order.Amount, ct);
        var audit = await _audit.WriteAsync("CHECKOUT_SUCCESS", "success", true, ct);

        _hooks.AfterCommit(() => _collector.Record("CheckoutService.AfterCommit: all services committed — checkout complete ✓"));

        return new CheckoutResult(order, reservation, payment, audit);
    }

    /// <summary>
    /// Scenario 2 — Payment failure.
    /// PaymentService.FailCardDeclinedAsync throws PaymentDeclinedException BEFORE SaveChanges.
    /// The outer Required scope disposes without Complete() — observer receives OnRollback.
    /// AfterRollback hooks registered in CheckoutService and PaymentService all fire.
    /// No data is written to the database (throw happens before any SaveChanges in this scenario).
    /// </summary>
    [Transactional]
    public async Task ProcessWithPaymentFailureAsync(CancellationToken ct = default)
    {
        _collector.Record("CheckoutService: outer Required scope opened");

        _hooks.AfterRollback(() => _collector.Record("CheckoutService.AfterRollback: payment failed — order never placed"));
        _hooks.AfterCompletion(() => _collector.Record("CheckoutService.AfterCompletion: checkout ended on rollback path"));

        await _payments.FailCardDeclinedAsync(0, 99.99m, ct);
    }

    /// <summary>
    /// Scenario 3 — Inventory failure.
    /// InventoryService.FailOutOfStockAsync throws before SaveChanges.
    /// Same rollback lifecycle as payment failure — nothing reaches the database.
    /// </summary>
    [Transactional]
    public async Task ProcessWithInventoryFailureAsync(CancellationToken ct = default)
    {
        _collector.Record("CheckoutService: outer Required scope opened");

        _hooks.AfterRollback(() => _collector.Record("CheckoutService.AfterRollback: inventory unavailable — checkout aborted"));
        _hooks.AfterCompletion(() => _collector.Record("CheckoutService.AfterCompletion: checkout ended on rollback path"));

        await _inventory.FailOutOfStockAsync("PROD-001", ct);
    }

    /// <summary>
    /// Scenario 4 — RequiresNew audit survives outer rollback.
    /// An order entity is added to the EF change tracker (but SaveChanges is NOT called in the outer scope).
    /// AuditService.WriteAsync opens an independent RequiresNew scope and commits the audit entry.
    /// The outer scope then throws and rolls back — the tracked order change is discarded.
    /// Result: AuditEntry persists, no order in the database.
    ///
    /// On SQL Server / PostgreSQL: even if SaveChanges had been called, the outer-scope write
    /// would be rolled back atomically while the audit entry (committed in RequiresNew) survives.
    /// On SQLite: the proxy lifecycle and hook behaviour are exercised correctly.
    /// </summary>
    [Transactional]
    public async Task ProcessWithAuditRequiresNewAsync(CancellationToken ct = default)
    {
        _collector.Record("CheckoutService: outer Required scope opened");

        _hooks.AfterRollback(() => _collector.Record("CheckoutService.AfterRollback: outer scope rolled back — tracked order change discarded, audit already committed in RequiresNew"));

        // Create an order inside the outer Required scope.
        // On SQL Server / PostgreSQL: this write is held within the ambient transaction and
        // rolled back atomically when the outer scope throws without Complete().
        // On SQLite (no ambient enlistment): SaveChanges commits immediately regardless, so the
        // order WILL appear in the database — but the proxy lifecycle and hook behavior are still
        // exercised correctly. See Known Limitations in README.
        var order = await _orders.CreateAsync("audit-requires-new", 49.99m, ct);
        _collector.Record($"CheckoutService: order #{order.Id} created inside outer Required scope");

        // RequiresNew: suspends the outer scope, opens an independent scope, commits.
        // This AuditEntry is durable regardless of what the outer scope does next.
        var audit = await _audit.WriteAsync("CHECKOUT_FAILED", "audit-requires-new", false, ct);
        _collector.Record($"CheckoutService: AuditEntry #{audit.Id} committed in RequiresNew — now throwing to roll back outer scope");

        throw new InvalidOperationException("Simulated business failure after audit was written");
    }

    /// <summary>
    /// Scenario 5 — NoRollbackFor.
    /// [Transactional(NoRollbackFor = [typeof(NotificationException)])] causes the proxy to call
    /// scope.Complete() when NotificationException is thrown, instead of disposing without it.
    ///
    /// Writes are made directly via _db (not through inner proxied services) so the data is
    /// unambiguously inside the outer Required scope.
    /// On SQL Server / PostgreSQL: scope.Complete() is what makes the data durable — without it,
    /// the ambient transaction would roll back and no records would persist.
    /// On SQLite (no ambient enlistment): SaveChanges commits immediately, but scope.Complete()
    /// is still called by the proxy — confirming the NoRollbackFor path is exercised correctly.
    /// </summary>
    [Transactional(NoRollbackFor = [typeof(NotificationException)])]
    public async Task<CheckoutResult> ProcessWithNoRollbackForAsync(CancellationToken ct = default)
    {
        _collector.Record("CheckoutService: scope opened with NoRollbackFor=[NotificationException]");

        var order = new CheckoutOrder
        {
            Scenario = "no-rollback-for",
            Status = "created",
            Amount = 59.99m,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _db.Orders.Add(order);
        await _db.SaveChangesAsync(ct);
        _collector.Record($"CheckoutService: order #{order.Id} saved inside outer scope");

        var payment = new PaymentRecord
        {
            OrderId = order.Id,
            Amount = order.Amount,
            Status = "approved",
            ProcessedAt = DateTimeOffset.UtcNow
        };
        _db.Payments.Add(payment);
        await _db.SaveChangesAsync(ct);
        _collector.Record($"CheckoutService: payment #{payment.Id} saved inside outer scope");

        _hooks.AfterCommit(() => _collector.Record("CheckoutService.AfterCommit: scope COMMITTED despite NotificationException — NoRollbackFor in effect ✓"));

        throw new NotificationException("Email service unavailable — this exception triggers commit, not rollback");
    }

    /// <summary>
    /// Scenario 6 — AfterCommit hook.
    /// PaymentService registers an AfterCommit hook that publishes a payment event.
    /// The hook does NOT fire when PaymentService.ProcessAsync returns — it fires only
    /// after the outer CheckoutService scope calls scope.Complete().
    /// This guarantees events are published only after data is durably committed.
    /// </summary>
    [Transactional]
    public async Task<CheckoutResult> ProcessWithAfterCommitHookAsync(CancellationToken ct = default)
    {
        _collector.Record("CheckoutService: outer Required scope opened — inner hooks fire when THIS scope commits");

        var order = await _orders.CreateAsync("after-commit-hook", 149.99m, ct);
        var payment = await _payments.ProcessAsync(order.Id, order.Amount, ct);

        _hooks.AfterCommit(() => _collector.Record("CheckoutService.AfterCommit: all inner service hooks have now fired (event bus received events)"));

        return new CheckoutResult(order, null, payment, null);
    }

    /// <summary>
    /// Scenario 7 — AfterRollback hooks as compensating actions.
    /// Three hooks registered: two sync, one async. All execute in order even if one throws.
    /// Demonstrates using hooks for compensation: releasing allocations, alerting ops, updating status.
    /// </summary>
    // Synchronous throw — the proxy routes this through the async wrapper because the return type is Task.
    [Transactional]
    public Task ProcessWithAfterRollbackHookAsync(CancellationToken ct = default)
    {
        _collector.Record("CheckoutService: outer Required scope opened");

        _hooks.AfterRollback(() => _collector.Record("CheckoutService.AfterRollback[1] sync: releasing warehouse stock allocation"));
        _hooks.AfterRollback(async () =>
        {
            // Async hooks can perform real async I/O (HTTP calls, queue writes, etc.)
            // Simulates a non-blocking alert to an operations monitoring endpoint.
            await Task.Delay(1, ct); // placeholder for: await _alertService.NotifyAsync(...)
            _collector.Record("CheckoutService.AfterRollback[2] async: alert sent to operations team via async I/O");
        });
        _hooks.AfterRollback(() => _collector.Record("CheckoutService.AfterRollback[3] sync: updating order status to checkout_failed"));
        _hooks.AfterCompletion(() => _collector.Record("CheckoutService.AfterCompletion: closing telemetry span"));

        throw new InvalidOperationException("Unexpected system error — rollback and compensating actions triggered");
    }

    /// <summary>
    /// Scenario 8 — Suppress propagation.
    /// InventoryReportService.ReadAvailableStockAsync uses [Transactional(Suppress)].
    /// The outer Required scope is suspended while this call runs — Transaction.Current is null inside it.
    /// After ReadAvailableStockAsync returns, the outer scope is automatically resumed.
    /// </summary>
    [Transactional]
    public async Task<CheckoutResult> ProcessWithSuppressAsync(CancellationToken ct = default)
    {
        _collector.Record("CheckoutService: outer Required scope active — about to call Suppress service");

        var order = await _orders.CreateAsync("suppress", 79.99m, ct);

        _collector.Record("CheckoutService: calling InventoryReportService (Suppress) — outer scope will be suspended");
        var report = await _inventoryReport.ReadAvailableStockAsync(ct);
        _collector.Record("CheckoutService: Suppress scope exited — outer Required scope resumed");

        var payment = await _payments.ProcessAsync(order.Id, order.Amount, ct);

        _hooks.AfterCommit(() => _collector.Record("CheckoutService.AfterCommit: committed — Suppress read did not affect transaction outcome"));

        return new CheckoutResult(order, null, payment, null, report);
    }
}
