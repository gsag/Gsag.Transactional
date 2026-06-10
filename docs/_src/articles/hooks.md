# Transaction Hooks

Hooks let you register callbacks that run at specific points in the transaction lifecycle. They are accessed via `ITransactionHooks`, which is registered as a singleton by `AddTransactional`.

```csharp
public class OrderService : IOrderService
{
    private readonly ITransactionHooks _hooks;

    public OrderService(ITransactionHooks hooks) => _hooks = hooks;

    [Transactional]
    public async Task PlaceOrderAsync(Order order)
    {
        // ... business logic ...
        _hooks.AfterCommit(() => _eventBus.Publish(new OrderPlaced(order.Id)));
    }
}
```

---

## Hook reference

| Hook | Sync | Async | Fires when | Exceptions |
|---|---|---|---|---|
| `BeforeCommit` | ✓ | ✓ | Inside scope, just before `scope.Complete()` | Propagate — trigger rollback |
| `BeforeRollback` | ✓ | ✓ | Inside scope, just before dispose on rollback path | Suppressed |
| `AfterCommit` | ✓ | ✓ | After scope is disposed — transaction committed | Propagate to caller |
| `AfterRollback` | ✓ | ✓ | After scope is disposed — transaction rolled back | Suppressed |
| `AfterCompletion` | ✓ | ✓ | After scope is disposed — always fires (commit or rollback) | Suppressed on rollback path; propagate on commit path |

---

## Commit path

```
  BeforeCommit (sync)     ← inside scope, throws trigger rollback
  BeforeCommit (async)    ← inside scope, throws trigger rollback
        │
  scope.Complete()
  scope.Dispose()         ← transaction committed
        │
  AfterCommit (sync)      ← outside scope
  AfterCommit (async)
  AfterCompletion (sync)
  AfterCompletion (async)
```

---

## Rollback path

```
  BeforeRollback (sync)   ← inside scope, exceptions suppressed
  BeforeRollback (async)  ← inside scope, exceptions suppressed
        │
  scope.Dispose()         ← no Complete(), transaction rolled back
        │
  AfterRollback (sync)    ← outside scope, exceptions suppressed
  AfterRollback (async)
  AfterCompletion (sync)
  AfterCompletion (async)
```

Within each event, sync hooks always execute **before** async hooks.

---

## BeforeCommit — validate or enrich before persisting

`BeforeCommit` hooks run **inside the open scope**, so any database writes they make are part of the same transaction.

```csharp
[Transactional]
public async Task PlaceOrderAsync(Order order)
{
    _db.Orders.Add(order);
    await _db.SaveChangesAsync();

    _hooks.BeforeCommit(async () =>
    {
        // This write is inside the same scope — commits or rolls back with the order.
        _db.AuditLog.Add(new AuditEntry("order-saved", order.Id));
        await _db.SaveChangesAsync();
    });
}
```

If a `BeforeCommit` hook throws, the exception propagates and the scope is rolled back instead of committed.

---

## AfterCommit — publish events after the transaction is durable

`AfterCommit` hooks run **after** the scope is disposed. This is the correct place to publish integration events — you know the data is committed before any subscriber can react to it.

```csharp
[Transactional]
public async Task PlaceOrderAsync(Order order)
{
    _db.Orders.Add(order);
    await _db.SaveChangesAsync();

    _hooks.AfterCommit(async () =>
    {
        await _eventBus.PublishAsync(new OrderPlaced(order.Id));
    });
}
```

---

## AfterRollback — compensating actions

Use `AfterRollback` to undo out-of-band side effects (cache evictions, external API calls) that happened during the transaction body.

```csharp
[Transactional]
public async Task ReserveInventoryAsync(int productId, int qty)
{
    await _inventoryApi.ReserveAsync(productId, qty);

    _hooks.AfterRollback(() =>
    {
        // Release the external reservation if the transaction rolls back.
        _inventoryApi.ReleaseAsync(productId, qty);
    });
}
```

---

## AfterCompletion — cleanup that must always run

`AfterCompletion` fires on both commit and rollback. Use it for cleanup that must happen regardless of the outcome: clearing caches, releasing resources, updating metrics.

```csharp
_hooks.AfterCompletion(() => _cache.Invalidate(order.Id));
```

---

## Async hooks on sync call paths

Async hook overloads (`Func<Task>`) require the method to return `Task`, `Task<T>`, `ValueTask`, or `ValueTask<T>`. Registering an async hook inside a **synchronous** `[Transactional]` method throws `NotSupportedException`:

```
Async hooks cannot be awaited on a synchronous [Transactional] call path.
Change the method return type to Task, Task<T>, ValueTask, or ValueTask<T>.
```

The check happens at execution time (before any hook runs), so no partial side effects occur.

---

## Nested scope hook isolation

When an inner `Required` service **joins** an existing ambient scope, its hooks accumulate in the **outer** scope's collection and fire when the outer scope commits — not when the inner call returns.

When an inner `RequiresNew` scope opens, it gets its own isolated hook collection. Hooks registered inside fire when the inner scope commits, independently of the outer scope.

When a `Suppress` scope runs, `Transaction.Current` is `null`, so hook registrations are no-ops — they are silently discarded.
