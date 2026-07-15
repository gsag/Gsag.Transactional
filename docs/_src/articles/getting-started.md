# Getting Started

This guide walks through adding `[Transactional]` to a service from scratch.

---

## 1. Define the interface

```csharp
public interface IOrderService
{
    Task PlaceOrderAsync(Order order);
}
```

---

## 2. Implement the concrete class

Place `[Transactional]` on the **concrete method**, not on the interface:

```csharp
using Gsag.Transactional.Core.Attributes;

public class OrderService : IOrderService
{
    private readonly AppDbContext _db;

    public OrderService(AppDbContext db) => _db = db;

    [Transactional]
    public async Task PlaceOrderAsync(Order order)
    {
        _db.Orders.Add(order);
        await _db.SaveChangesAsync();
        // TransactionScope commits here — on success only.
        // Any exception rolls back automatically.
    }
}
```

> **Why the concrete class?**  
> The interface stays a clean contract. The proxy resolves the attribute from the concrete method via `GetInterfaceMap`, so the attribute works either way — but placing it on the concrete class keeps the interface free of infrastructure concerns.

---

## 3. Register with DI

```csharp
// Program.cs
builder.Services.AddTransactional(b => b.AddLogging());
```

The **calling assembly is automatically scanned** to find `OrderService` and its interface `IOrderService`, then registers `IOrderService` as a `DispatchProxy`-wrapped transactional service. When you inject `IOrderService`, you receive the proxy.

To scan a **different assembly**, use `ScanAssembly()`. Note: calling `ScanAssembly()` **overwrites the default behavior** — only the specified assembly is scanned:

```csharp
// Only SomeAssembly is scanned; calling assembly is NOT scanned
builder.Services.AddTransactional(b => b
    .ScanAssembly(typeof(SomeService).Assembly)
    .AddLogging()
);
```

To scan **multiple assemblies**, call `ScanAssembly()` multiple times:

```csharp
builder.Services.AddTransactional(b => b
    .ScanAssembly(typeof(SomeService).Assembly)
    .ScanAssembly(typeof(OtherService).Assembly)
    .AddLogging()
);
```

---

## 4. Inject and call

```csharp
public class CheckoutController : ControllerBase
{
    private readonly IOrderService _orders;

    public CheckoutController(IOrderService orders) => _orders = orders;

    [HttpPost]
    public async Task<IActionResult> Checkout(CheckoutRequest request)
    {
        await _orders.PlaceOrderAsync(request.ToOrder());
        return Ok();
    }
}
```

---

## What happens at runtime

```
Caller injects IOrderService
  └─ receives TransactionProxy<IOrderService>
       │
       ▼  [on PlaceOrderAsync call]
  new TransactionScope(Required, ReadCommitted)
       │
       ▼
  OrderService.PlaceOrderAsync executes
       │
       ├─ success → scope.Complete() → Dispose() → committed ✓
       └─ exception → Dispose() without Complete() → rolled back ✗
```

The scope is created **before** the method body runs, ensuring any EF Core `DbConnection` opened inside the method enlists in the ambient transaction.

---

## Next steps

- [Propagation Modes](propagation.md) — `RequiresNew` for independent inner scopes, `Suppress` for non-transactional reads
- [Transaction Hooks](hooks.md) — run code after commit or rollback
- [Rollback Rules](rollback-rules.md) — fine-tune which exceptions trigger rollback
