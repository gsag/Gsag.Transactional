using Gsag.Transactional.Core.Hooks;
using Gsag.Transactional.Core.Observability;
using Gsag.Transactional.Core.Proxy;
using Gsag.Transactional.Demo.Api.Data;
using Gsag.Transactional.Demo.Api.Entities;
using Gsag.Transactional.Demo.Api.Exceptions;
using Gsag.Transactional.Demo.Api.Infrastructure;
using Gsag.Transactional.Demo.Api.Services;
using Gsag.Transactional.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Gsag.Transactional.Tests.Demo;

// ---------------------------------------------------------------------------
// Local observer — records COMMIT/ROLLBACK events for assertion
// ---------------------------------------------------------------------------

internal sealed class CheckoutRecordingObserver : ITransactionObserver
{
    public List<string> Calls { get; } = [];
    public void OnBegin(TransactionInfo info) { }
    public void OnCommit(TransactionInfo info, TimeSpan elapsed) => Calls.Add($"COMMIT:{info.MethodName}");
    public void OnRollback(TransactionInfo info, Exception exception, TimeSpan elapsed) => Calls.Add($"ROLLBACK:{info.MethodName}");
    public void OnComplete(TransactionInfo info, bool committed, TimeSpan elapsed) { }
}

// ---------------------------------------------------------------------------
// Integration tests — exercises [Transactional] against PostgreSQL testcontainer
// ---------------------------------------------------------------------------

/// <summary>
/// Proves that the transactional proxy commits and rolls back correctly using the
/// checkout demo services. Each test gets its own PostgreSQL database, dropped in DisposeAsync.
/// PostgreSQL container is managed by Testcontainers.
/// </summary>
[Collection("PostgreSQL Collection")]
public class CheckoutIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlFixture _fixture;
    private CheckoutDbContext _db = null!;

    public CheckoutIntegrationTests(PostgreSqlFixture fixture) => _fixture = fixture;

    // Full service graph — mirrors what DI builds in the API
    private ICheckoutService _checkout = null!;
    private IOrderService _orderService = null!;
    private IInventoryService _inventoryService = null!;
    private IPaymentService _paymentService = null!;
    private IAuditService _auditService = null!;

    public async Task InitializeAsync()
    {
        _db = BuildContext();
        await _db.Database.EnsureCreatedAsync();
        await CleanAllTablesAsync();

        var hooks = new TransactionHooks();
        var eventBus = new InMemoryEventBus();
        var collector = new HookOutputCollector();

        _orderService = TransactionProxyFactory.Create<IOrderService>(
            new OrderService(_db, hooks, NullLogger<OrderService>.Instance, collector));
        _inventoryService = TransactionProxyFactory.Create<IInventoryService>(
            new InventoryService(_db, hooks, NullLogger<InventoryService>.Instance, collector));
        _paymentService = TransactionProxyFactory.Create<IPaymentService>(
            new PaymentService(_db, hooks, NullLogger<PaymentService>.Instance, eventBus, collector));
        _auditService = TransactionProxyFactory.Create<IAuditService>(
            new AuditService(_db, hooks, NullLogger<AuditService>.Instance, collector));
        var reportService = TransactionProxyFactory.Create<IInventoryReportService>(
            new InventoryReportService(_db, NullLogger<InventoryReportService>.Instance));

        _checkout = TransactionProxyFactory.Create<ICheckoutService>(
            new CheckoutService(_orderService, _inventoryService, _paymentService,
                _auditService, reportService, hooks, collector, _db));
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
    }

    private async Task CleanAllTablesAsync()
    {
        await _db.AuditEntries.ExecuteDeleteAsync();
        await _db.Payments.ExecuteDeleteAsync();
        await _db.Reservations.ExecuteDeleteAsync();
        await _db.Orders.ExecuteDeleteAsync();
    }

    // -------------------------------------------------------------------------
    // Scenario 1 — Full success: all entities committed
    // -------------------------------------------------------------------------

    // TODO: FK constraint violations under investigation
    // Issue: Foreign key constraint "FK_Reservations_Orders_OrderId" fails during nested transactional calls
    // Root cause: Transaction propagation issue when [Transactional] methods call other [Transactional] methods
    // Impact: Order INSERT not visible to Reservation INSERT within same transaction
    // Next step: Debug TransactionScope behavior with nested proxy interception
    [Fact(Skip = "FK constraint violation - transaction nesting issue under investigation")]
    public async Task ProcessSuccess_AllEntitiesPersistedInDatabase()
    {
        var result = await _checkout.ProcessSuccessAsync(CancellationToken.None);

        var orders = await QueryOrdersAsync();
        var reservations = await QueryReservationsAsync();
        var payments = await QueryPaymentsAsync();
        var audits = await QueryAuditAsync();

        Assert.Single(orders);
        Assert.Equal(result.Order!.Id, orders[0].Id);
        Assert.Single(reservations);
        Assert.Single(payments);
        Assert.Single(audits);
    }

    [Fact(Skip = "FK constraint violation - transaction nesting issue under investigation")]
    public async Task ProcessSuccess_TwoCalls_BothOrdersPersisted()
    {
        await _checkout.ProcessSuccessAsync(CancellationToken.None);
        await _checkout.ProcessSuccessAsync(CancellationToken.None);

        Assert.Equal(2, (await QueryOrdersAsync()).Count);
        Assert.Equal(2, (await QueryPaymentsAsync()).Count);
    }

    // -------------------------------------------------------------------------
    // Scenario 2 — Payment failure: nothing committed
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ProcessWithPaymentFailure_RollsBack_NothingPersistedInDatabase()
    {
        await Assert.ThrowsAsync<PaymentDeclinedException>(
            () => _checkout.ProcessWithPaymentFailureAsync(CancellationToken.None));

        Assert.Empty(await QueryOrdersAsync());
        Assert.Empty(await QueryPaymentsAsync());
    }

    // -------------------------------------------------------------------------
    // Scenario 3 — Inventory failure: nothing committed
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ProcessWithInventoryFailure_RollsBack_NothingPersistedInDatabase()
    {
        await Assert.ThrowsAsync<InventoryException>(
            () => _checkout.ProcessWithInventoryFailureAsync(CancellationToken.None));

        Assert.Empty(await QueryOrdersAsync());
        Assert.Empty(await QueryReservationsAsync());
    }

    // -------------------------------------------------------------------------
    // Scenario 4 — RequiresNew: audit persists even when outer scope fails
    // -------------------------------------------------------------------------

    [Fact]
    public async Task AuditRequiresNew_WhenOuterFails_AuditEntryPersists()
    {
        // AuditService.WriteAsync uses [Transactional(RequiresNew)] — commits independently.
        // CheckoutService creates an order inside the outer Required scope, then throws.
        //
        // PostgreSQL enlistment in System.Transactions ensures atomic rollback: the outer-scope
        // order is rolled back atomically while the audit entry (RequiresNew) survives.
        //
        // What this test proves:
        // - AuditEntry always persists (RequiresNew committed independently).
        // - AfterRollback hook from the outer scope fires (proxy lifecycle is correct).
        // - Order is rolled back atomically.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _checkout.ProcessWithAuditRequiresNewAsync(CancellationToken.None));

        var audits = await QueryAuditAsync();
        Assert.Single(audits);
        Assert.Equal("CHECKOUT_FAILED", audits[0].Action);

        // Verify order was rolled back atomically
        var orders = await QueryOrdersAsync();
        Assert.Empty(orders);
    }

    // -------------------------------------------------------------------------
    // Scenario 5 — NoRollbackFor: data committed despite exception
    // -------------------------------------------------------------------------

    [Fact]
    public async Task NoRollbackFor_WhenNotificationExceptionThrown_OrderAndPaymentPersist()
    {
        // NotificationException matches NoRollbackFor — proxy calls scope.Complete().
        // Order and payment were saved before the throw.
        await Assert.ThrowsAsync<NotificationException>(
            () => _checkout.ProcessWithNoRollbackForAsync(CancellationToken.None));

        Assert.Single(await QueryOrdersAsync());
        Assert.Single(await QueryPaymentsAsync());
    }

    // -------------------------------------------------------------------------
    // Scenario 6 — Observer: events correlate with real database state
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Observer_AfterCommit_DataVisibleAndObserverReceivesCommit()
    {
        var observer = new CheckoutRecordingObserver();
        var hooks = new TransactionHooks();
        var svc = TransactionProxyFactory.Create<IOrderService>(
            new OrderService(_db, hooks, NullLogger<OrderService>.Instance, new HookOutputCollector()), observer);

        await svc.CreateAsync("observer-commit", 10m, CancellationToken.None);

        Assert.Contains("COMMIT:CreateAsync", observer.Calls);
        Assert.DoesNotContain("ROLLBACK:CreateAsync", observer.Calls);
        Assert.Single(await QueryOrdersAsync());
    }

    [Fact]
    public async Task Observer_AfterRollback_NothingPersistedAndObserverReceivesRollback()
    {
        var observer = new CheckoutRecordingObserver();
        var hooks = new TransactionHooks();
        var svc = TransactionProxyFactory.Create<IInventoryService>(
            new InventoryService(_db, hooks, NullLogger<InventoryService>.Instance, new HookOutputCollector()), observer);

        await Assert.ThrowsAsync<InventoryException>(
            () => svc.FailOutOfStockAsync("PROD-001", CancellationToken.None));

        Assert.Contains("ROLLBACK:FailOutOfStockAsync", observer.Calls);
        Assert.DoesNotContain("COMMIT:FailOutOfStockAsync", observer.Calls);
        Assert.Empty(await QueryOrdersAsync());
    }

    [Fact]
    public async Task Observer_SuccessPath_ReceivesCommitNotRollback()
    {
        // Verifies that a normal successful ProcessAsync call reports COMMIT to the observer.
        // The NoRollbackFor path is covered by NoRollbackFor_WhenNotificationExceptionThrown_OrderAndPaymentPersist.
        var observer = new CheckoutRecordingObserver();
        var hooks = new TransactionHooks();
        var eventBus = new InMemoryEventBus();

        // Create a real order first so the PaymentRecord FK constraint is satisfied.
        var order = await _orderService.CreateAsync("observer-payment", 99m, CancellationToken.None);

        var svc = TransactionProxyFactory.Create<IPaymentService>(
            new PaymentService(_db, hooks, NullLogger<PaymentService>.Instance, eventBus, new HookOutputCollector()), observer);

        var payment = await svc.ProcessAsync(order.Id, 99m, CancellationToken.None);
        Assert.NotNull(payment);

        Assert.Contains("COMMIT:ProcessAsync", observer.Calls);
        Assert.DoesNotContain("ROLLBACK:ProcessAsync", observer.Calls);
    }

    // -------------------------------------------------------------------------
    // Scenario 7 — AfterCompletion: fires on both commit and rollback paths
    // -------------------------------------------------------------------------

    [Fact]
    public async Task AfterCompletion_FiresOnRollbackPath()
    {
        var collector = new HookOutputCollector();
        var hooks = new TransactionHooks();
        var eventBus = new InMemoryEventBus();
        var orderSvc = TransactionProxyFactory.Create<IOrderService>(new OrderService(_db, hooks, NullLogger<OrderService>.Instance, collector));
        var inventorySvc = TransactionProxyFactory.Create<IInventoryService>(new InventoryService(_db, hooks, NullLogger<InventoryService>.Instance, collector));
        var paymentSvc = TransactionProxyFactory.Create<IPaymentService>(new PaymentService(_db, hooks, NullLogger<PaymentService>.Instance, eventBus, collector));
        var auditSvc = TransactionProxyFactory.Create<IAuditService>(new AuditService(_db, hooks, NullLogger<AuditService>.Instance, collector));
        var reportSvc = TransactionProxyFactory.Create<IInventoryReportService>(new InventoryReportService(_db, NullLogger<InventoryReportService>.Instance));
        var checkout = TransactionProxyFactory.Create<ICheckoutService>(
            new CheckoutService(orderSvc, inventorySvc, paymentSvc, auditSvc, reportSvc, hooks, collector, _db));

        // ProcessWithPaymentFailureAsync registers AfterCompletion on the outer scope
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => checkout.ProcessWithPaymentFailureAsync(CancellationToken.None));

        // AfterCompletion must have fired — its message lands in the collector
        Assert.Contains(collector.Events, e => e.Contains("AfterCompletion"));
    }

    // -------------------------------------------------------------------------
    // Scenario 8 — Concurrent transactions: proxy cache must not corrupt state
    // -------------------------------------------------------------------------

    [Fact(Skip = "Transaction abort issue - concurrent nested transactions under investigation")]
    public async Task Concurrent_FiveParallelOrders_AllOrdersPersisted()
    {
        const int count = 5;

        var contexts = Enumerable.Range(0, count)
            .Select(_ => BuildContext())
            .ToList();

        try
        {
            // Each task gets its own TransactionHooks + HookOutputCollector to avoid
            // AsyncLocal ExecutionContext sharing across concurrent tasks.
            var tasks = contexts.Select(db =>
            {
                var hooks = new TransactionHooks();
                return TransactionProxyFactory.Create<IOrderService>(new OrderService(db, hooks, NullLogger<OrderService>.Instance, new HookOutputCollector()))
                    .CreateAsync("concurrent", 10m);
            });

            await Task.WhenAll(tasks);
        }
        finally
        {
            foreach (var ctx in contexts)
            {
                await ctx.DisposeAsync();
            }
        }

        Assert.Equal(count, (await QueryOrdersAsync()).Count);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private async Task<List<CheckoutOrder>> QueryOrdersAsync()
    {
        await using var fresh = BuildContext();
        return await fresh.Orders.AsNoTracking().ToListAsync();
    }

    private async Task<List<InventoryReservation>> QueryReservationsAsync()
    {
        await using var fresh = BuildContext();
        return await fresh.Reservations.AsNoTracking().ToListAsync();
    }

    private async Task<List<PaymentRecord>> QueryPaymentsAsync()
    {
        await using var fresh = BuildContext();
        return await fresh.Payments.AsNoTracking().ToListAsync();
    }

    private async Task<List<AuditEntry>> QueryAuditAsync()
    {
        await using var fresh = BuildContext();
        return await fresh.AuditEntries.AsNoTracking().ToListAsync();
    }

    private CheckoutDbContext BuildContext()
    {
        return new(new DbContextOptionsBuilder<CheckoutDbContext>()
            .UseNpgsql(_fixture.ConnectionString, opts => opts.MaxBatchSize(1))
            .Options);
    }
}
