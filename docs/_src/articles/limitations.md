# Limitations

These are known constraints of the `DispatchProxy`-based approach. Most have straightforward workarounds.

---

## 1. Must be injected as an interface

`DispatchProxy` can only intercept calls made through an **interface**. Injecting the concrete class directly bypasses the proxy.

```csharp
// WRONG — receives the unwrapped OrderService, no scope is created
public class CheckoutService(OrderService orders) { }

// CORRECT — receives TransactionProxy<IOrderService>
public class CheckoutService(IOrderService orders) { }
```

**Why:** `DispatchProxy.Create<T>()` requires `T` to be an interface. There is no equivalent mechanism for concrete classes without external AOP tooling.

---

## 2. Only interface-typed registrations are proxied

`AddTransactional` registers services as `Scoped` under their **interface** type and wraps the implementation in a proxy. If you resolve the concrete type directly (e.g., via `[FromKeyedServices]` or a factory), you receive the unwrapped instance.

**Workaround:** Always consume services through the interface type in DI. Use `.AddService<IFoo, FooService>()` for non-convention registrations.

---

## 3. Self-invocation bypasses the proxy

Calling a `[Transactional]` method via `this.Method()` from within the same class sends the call directly to the concrete instance — `DispatchProxy` is not in the call chain.

```csharp
public class OrderService : IOrderService
{
    [Transactional]
    public async Task PlaceOrderAsync(Order order)
    {
        await WriteAuditAsync(order); // ← bypasses proxy, no scope is opened
    }

    [Transactional(Propagation = TransactionScopeOption.RequiresNew)]
    public async Task WriteAuditAsync(Order order) { ... }
}
```

**Workaround:** Extract the inner method to a separate service and inject it as an interface.

---

## 4. Only async and void-returning methods are fully supported

`[Transactional]` supports:
- `Task`
- `Task<T>`
- `ValueTask`
- `ValueTask<T>`
- Synchronous (non-async) return types — runs synchronously, no async hook support

Methods returning other types (e.g., `IAsyncEnumerable<T>`, custom awaitables) are not intercepted and are forwarded directly to the target.

---

## 5. Async hooks cannot be used on synchronous call paths

Registering a `Func<Task>` hook inside a method with a synchronous return type throws `NotSupportedException` at execution time, before any hook runs:

```
Async hooks cannot be awaited on a synchronous [Transactional] call path.
Change the method return type to Task, Task<T>, ValueTask, or ValueTask<T>.
```

**Workaround:** Change the method signature to return `Task` (or `ValueTask`), or use the synchronous `Action` overloads.

---

## 6. EF Core SQLite does not support ambient transactions

EF Core 9's SQLite provider sets `SupportsAmbientTransactions = false`. This means `DbConnection` does not enlist in the `TransactionScope`, so database-level rollback cannot be tested with SQLite in integration tests.

**Workaround:** Throw before `SaveChangesAsync()` to test rollback semantics at the proxy level. For full database rollback tests, use a provider that supports ambient transactions (SQL Server, PostgreSQL via Npgsql).

---

## 7. BeforeRollback cannot vote to commit

`BeforeRollback` hooks run on the rollback path — the decision to roll back has already been made. Throwing inside a `BeforeRollback` hook does not change the outcome; the exception is suppressed and the rollback proceeds.

If you need to intercept before a rollback decision is made, register a `BeforeCommit` hook instead (it can cause a rollback by throwing).
