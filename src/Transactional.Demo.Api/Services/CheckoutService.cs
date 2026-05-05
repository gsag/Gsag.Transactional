using Transactional.Core.Attributes;
using Transactional.Core.Hooks;
using Transactional.Demo.Api.Exceptions;
using Transactional.Demo.Api.Infrastructure;

namespace Transactional.Demo.Api.Services;

public class CheckoutService : ICheckoutService
{
    private readonly IOrderService _orders;
    private readonly IInventoryService _inventory;
    private readonly IPaymentService _payments;
    private readonly IAuditService _audit;
    private readonly IInventoryReportService _inventoryReport;
    private readonly ITransactionHooks _hooks;
    private readonly HookOutputCollector _collector;

    public CheckoutService(
        IOrderService orders,
        IInventoryService inventory,
        IPaymentService payments,
        IAuditService audit,
        IInventoryReportService inventoryReport,
        ITransactionHooks hooks,
        HookOutputCollector collector)
    {
        _orders = orders;
        _inventory = inventory;
        _payments = payments;
        _audit = audit;
        _inventoryReport = inventoryReport;
        _hooks = hooks;
        _collector = collector;
    }

    /// <summary>
    /// Scenario 1 — Full success.
    /// CheckoutService opens a Required outer scope.
    /// OrderService, InventoryService, PaymentService each have [Transactional(Required)] and JOIN the outer scope.
    /// AuditService has [Transactional(RequiresNew)] — commits independently in its own scope.
    /// Hooks registered in all inner services fire when the OUTER scope commits (not when each inner method returns).
    /// </summary>
    [Transactional]
    public async Task<CheckoutResult> ProcessSuccessAsync()
    {
        _collector.Record("CheckoutService: outer Required scope opened");

        var order = await _orders.CreateAsync("success", 99.99m);
        var reservation = await _inventory.ReserveAsync(order.Id, "PROD-001", 1);
        var payment = await _payments.ProcessAsync(order.Id, order.Amount);
        var audit = await _audit.WriteAsync("CHECKOUT_SUCCESS", "success", true);

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
    public async Task ProcessWithPaymentFailureAsync()
    {
        _collector.Record("CheckoutService: outer Required scope opened");

        _hooks.AfterRollback(() => _collector.Record("CheckoutService.AfterRollback: payment failed — order never placed"));
        _hooks.AfterCompletion(() => _collector.Record("CheckoutService.AfterCompletion: checkout ended on rollback path"));

        await _payments.FailCardDeclinedAsync(0, 99.99m);
    }

    /// <summary>
    /// Scenario 3 — Inventory failure.
    /// InventoryService.FailOutOfStockAsync throws before SaveChanges.
    /// Same rollback lifecycle as payment failure — nothing reaches the database.
    /// </summary>
    [Transactional]
    public async Task ProcessWithInventoryFailureAsync()
    {
        _collector.Record("CheckoutService: outer Required scope opened");

        _hooks.AfterRollback(() => _collector.Record("CheckoutService.AfterRollback: inventory unavailable — checkout aborted"));
        _hooks.AfterCompletion(() => _collector.Record("CheckoutService.AfterCompletion: checkout ended on rollback path"));

        await _inventory.FailOutOfStockAsync("PROD-001");
    }

    /// <summary>
    /// Scenario 4 — RequiresNew audit survives outer rollback.
    /// AuditService.WriteAsync opens an independent RequiresNew scope and commits.
    /// The outer scope then throws and rolls back — the AuditEntry remains in the database.
    ///
    /// On SQL Server / PostgreSQL: any outer-scope writes (order, inventory, payment) would be
    /// rolled back while the audit entry persists. On SQLite (no ambient tx enlistment) the
    /// scope lifecycle and observer/hook behavior is exercised correctly.
    /// </summary>
    [Transactional]
    public async Task ProcessWithAuditRequiresNewAsync()
    {
        _collector.Record("CheckoutService: outer Required scope opened");

        _hooks.AfterRollback(() => _collector.Record("CheckoutService.AfterRollback: outer scope rolled back — audit was already committed in RequiresNew"));

        var audit = await _audit.WriteAsync("CHECKOUT_FAILED", "audit-requires-new", false);
        _collector.Record($"CheckoutService: AuditEntry #{audit.Id} committed in RequiresNew — now throwing to roll back outer scope");

        throw new InvalidOperationException("Simulated business failure after audit was written");
    }

    /// <summary>
    /// Scenario 5 — NoRollbackFor.
    /// [Transactional(NoRollbackFor = [typeof(NotificationException)])] causes the proxy to call
    /// scope.Complete() when NotificationException is thrown, instead of disposing without it.
    /// Order and payment are saved before the throw — both records persist despite the exception.
    /// </summary>
    [Transactional(NoRollbackFor = [typeof(NotificationException)])]
    public async Task<CheckoutResult> ProcessWithNoRollbackForAsync()
    {
        _collector.Record("CheckoutService: scope opened with NoRollbackFor=[NotificationException]");

        var order = await _orders.CreateAsync("no-rollback-for", 59.99m);
        var payment = await _payments.ProcessAsync(order.Id, order.Amount);

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
    public async Task<CheckoutResult> ProcessWithAfterCommitHookAsync()
    {
        _collector.Record("CheckoutService: outer Required scope opened — inner hooks fire when THIS scope commits");

        var order = await _orders.CreateAsync("after-commit-hook", 149.99m);
        var payment = await _payments.ProcessAsync(order.Id, order.Amount);

        _hooks.AfterCommit(() => _collector.Record("CheckoutService.AfterCommit: all inner service hooks have now fired (event bus received events)"));

        return new CheckoutResult(order, null, payment, null);
    }

    /// <summary>
    /// Scenario 7 — AfterRollback hooks as compensating actions.
    /// Three hooks registered: two sync, one async. All execute in order even if one throws.
    /// Demonstrates using hooks for compensation: releasing allocations, alerting ops, updating status.
    /// </summary>
    [Transactional]
    public async Task ProcessWithAfterRollbackHookAsync()
    {
        _collector.Record("CheckoutService: outer Required scope opened");

        _hooks.AfterRollback(() => _collector.Record("CheckoutService.AfterRollback[1] sync: releasing warehouse stock allocation"));
        _hooks.AfterRollback(async () =>
        {
            await Task.CompletedTask;
            _collector.Record("CheckoutService.AfterRollback[2] async: sending alert to operations team");
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
    public async Task<CheckoutResult> ProcessWithSuppressAsync()
    {
        _collector.Record("CheckoutService: outer Required scope active — about to call Suppress service");

        var order = await _orders.CreateAsync("suppress", 79.99m);

        _collector.Record("CheckoutService: calling InventoryReportService (Suppress) — outer scope will be suspended");
        var report = await _inventoryReport.ReadAvailableStockAsync();
        _collector.Record("CheckoutService: Suppress scope exited — outer Required scope resumed");

        var payment = await _payments.ProcessAsync(order.Id, order.Amount);

        _hooks.AfterCommit(() => _collector.Record("CheckoutService.AfterCommit: committed — Suppress read did not affect transaction outcome"));

        return new CheckoutResult(order, null, payment, null, report);
    }
}
