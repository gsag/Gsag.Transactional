# Propagation Modes

The `Propagation` property of `[Transactional]` controls how a method interacts with an existing ambient `TransactionScope`. It mirrors the propagation attribute from Spring's `@Transactional`.

```csharp
[Transactional(Propagation = TransactionScopeOption.RequiresNew)]
public async Task WriteAuditAsync(AuditEntry entry) { ... }
```

---

## Reference

| Mode | Value | Behaviour |
|---|---|---|
| **Required** | `TransactionScopeOption.Required` | Join the ambient transaction if one exists; create a new one otherwise. **Default.** |
| **RequiresNew** | `TransactionScopeOption.RequiresNew` | Always open a new, independent transaction. Suspend any ambient transaction for the duration. |
| **Suppress** | `TransactionScopeOption.Suppress` | Run outside any transaction. Suspend the ambient transaction; `Transaction.Current` is `null` inside the method. |

---

## Decision flow

```
Method called
│
├─ Ambient transaction exists?
│   ├─ Required     → join outer scope (hooks accumulate in outer collection)
│   ├─ RequiresNew  → suspend outer, open new independent scope
│   └─ Suppress     → suspend outer, Transaction.Current = null
│
└─ No ambient transaction?
    ├─ Required     → create new scope
    ├─ RequiresNew  → create new scope
    └─ Suppress     → run without scope, Transaction.Current = null
```

---

## Required (default)

All services that participate in the same logical unit of work should use `Required`. If the outer scope rolls back, every joined inner scope rolls back with it — they share the same underlying `CommittableTransaction`.

```csharp
// Outer service — opens the scope
public class CheckoutService : ICheckoutService
{
    private readonly IOrderService _orders;
    private readonly IPaymentService _payments;

    [Transactional]               // Required — creates new scope
    public async Task CheckoutAsync(CartDto cart)
    {
        await _orders.PlaceOrderAsync(cart);    // Required — joins outer scope
        await _payments.ChargeAsync(cart);      // Required — joins outer scope
        // If either throws, the entire outer scope rolls back.
    }
}
```

---

## RequiresNew — independent inner scope

Use `RequiresNew` when the inner operation must commit **regardless** of what happens to the outer scope. A common pattern is audit logging: you want to record that an operation was attempted even if the outer business transaction later fails.

```csharp
public class AuditService : IAuditService
{
    private readonly AuditDbContext _db;

    [Transactional(Propagation = TransactionScopeOption.RequiresNew)]
    public async Task WriteAsync(AuditEntry entry)
    {
        _db.Entries.Add(entry);
        await _db.SaveChangesAsync();
        // Commits immediately when this method returns — independently of the outer scope.
    }
}
```

```csharp
public class CheckoutService : ICheckoutService
{
    private readonly IAuditService _audit;

    [Transactional]
    public async Task CheckoutAsync(CartDto cart)
    {
        await _audit.WriteAsync(new AuditEntry("checkout-started"));
        // ...
        throw new PaymentDeclinedException(); // outer rolls back — audit entry survives
    }
}
```

---

## Suppress — non-transactional reads

Use `Suppress` when a method should run **outside** any transaction — for example, to avoid read locks on a reporting query or to call a resource that does not support distributed transactions.

```csharp
public class InventoryReportService : IInventoryReportService
{
    private readonly InventoryDbContext _db;

    [Transactional(Propagation = TransactionScopeOption.Suppress)]
    public async Task<int> ReadAvailableStockAsync(int productId)
    {
        // Transaction.Current is null here — no lock held on the outer transaction.
        return await _db.Inventory
            .Where(i => i.ProductId == productId)
            .Select(i => i.Available)
            .FirstOrDefaultAsync();
    }
}
```

---

## Self-invocation pitfall

Calling a `[Transactional]` method from **within the same class** bypasses the proxy entirely — the call goes directly to `this`, not through `DispatchProxy`.

```csharp
// WRONG — DoWorkAsync is called directly, no scope is created
public class OrderService : IOrderService
{
    [Transactional]
    public async Task PlaceOrderAsync(Order order)
    {
        await DoWorkAsync(order); // ← bypasses proxy
    }

    [Transactional(Propagation = TransactionScopeOption.RequiresNew)]
    public async Task DoWorkAsync(Order order) { ... }
}
```

**Fix:** extract the inner method to a separate service and inject it as an interface:

```csharp
public class OrderService : IOrderService
{
    private readonly IWorkService _work;

    [Transactional]
    public async Task PlaceOrderAsync(Order order)
    {
        await _work.DoWorkAsync(order); // ← goes through proxy ✓
    }
}
```
