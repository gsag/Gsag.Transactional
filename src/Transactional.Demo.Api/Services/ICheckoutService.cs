using Transactional.Demo.Api.Entities;

namespace Transactional.Demo.Api.Services;

public record CheckoutResult(
    CheckoutOrder? Order,
    InventoryReservation? Reservation,
    PaymentRecord? Payment,
    AuditEntry? Audit,
    InventoryReport? StockReport = null);

public interface ICheckoutService
{
    /// <summary>Scenario 1: Full success — all services commit, hooks fire.</summary>
    Task<CheckoutResult> ProcessSuccessAsync();

    /// <summary>Scenario 2: Card declined — outer scope rolls back, AfterRollback hooks fire.</summary>
    Task ProcessWithPaymentFailureAsync();

    /// <summary>Scenario 3: Out of stock — outer scope rolls back before any write.</summary>
    Task ProcessWithInventoryFailureAsync();

    /// <summary>Scenario 4: Outer fails, RequiresNew audit persists independently.</summary>
    Task ProcessWithAuditRequiresNewAsync();

    /// <summary>Scenario 5: NotificationException triggers commit (NoRollbackFor) — data persists.</summary>
    Task<CheckoutResult> ProcessWithNoRollbackForAsync();

    /// <summary>Scenario 6: AfterCommit hook — event published only after scope.Complete().</summary>
    Task<CheckoutResult> ProcessWithAfterCommitHookAsync();

    /// <summary>Scenario 7: AfterRollback hooks — compensating actions on failure.</summary>
    Task ProcessWithAfterRollbackHookAsync();

    /// <summary>Scenario 8: Suppress — inventory read runs outside the ambient transaction.</summary>
    Task<CheckoutResult> ProcessWithSuppressAsync();
}
