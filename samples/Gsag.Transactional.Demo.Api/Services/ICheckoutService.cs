using Gsag.Transactional.Demo.Api.Entities;

namespace Gsag.Transactional.Demo.Api.Services;

public record CheckoutResult(
    /// <remarks>Null on rollback-only scenarios (2, 3, 7) that never create an order.</remarks>
    CheckoutOrder? Order,
    /// <remarks>Only populated by scenario 1 (full success).</remarks>
    InventoryReservation? Reservation,
    /// <remarks>Null on rollback paths and inventory-failure scenario.</remarks>
    PaymentRecord? Payment,
    /// <remarks>Only populated by scenarios 1 and 4 (RequiresNew).</remarks>
    AuditEntry? Audit,
    /// <remarks>Only populated by scenario 8 (Suppress).</remarks>
    InventoryReport? StockReport = null);

public interface ICheckoutService
{
    /// <summary>Scenario 1: Full success — all services commit, hooks fire.</summary>
    Task<CheckoutResult> ProcessSuccessAsync(CancellationToken ct = default);

    /// <summary>Scenario 2: Card declined — outer scope rolls back, AfterRollback hooks fire.</summary>
    Task ProcessWithPaymentFailureAsync(CancellationToken ct = default);

    /// <summary>Scenario 3: Out of stock — outer scope rolls back before any write.</summary>
    Task ProcessWithInventoryFailureAsync(CancellationToken ct = default);

    /// <summary>Scenario 4: Outer fails, RequiresNew audit persists independently.</summary>
    Task ProcessWithAuditRequiresNewAsync(CancellationToken ct = default);

    /// <summary>Scenario 5: NotificationException triggers commit (NoRollbackFor) — data persists.</summary>
    Task<CheckoutResult> ProcessWithNoRollbackForAsync(CancellationToken ct = default);

    /// <summary>Scenario 6: AfterCommit hook — event published only after scope.Complete().</summary>
    Task<CheckoutResult> ProcessWithAfterCommitHookAsync(CancellationToken ct = default);

    /// <summary>Scenario 7: AfterRollback hooks — compensating actions on failure.</summary>
    Task ProcessWithAfterRollbackHookAsync(CancellationToken ct = default);

    /// <summary>Scenario 8: Suppress — inventory read runs outside the ambient transaction.</summary>
    Task<CheckoutResult> ProcessWithSuppressAsync(CancellationToken ct = default);
}
