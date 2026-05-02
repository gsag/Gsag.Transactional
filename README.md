# csharp-transactional-pattern

Demo solution that implements a custom `@Transactional`-like behavior in C# using **only native .NET features** — no external AOP libraries.

---

## The Problem

Java's Spring Framework lets you annotate a method with `@Transactional` and the framework automatically wraps it in a database transaction — committing on success, rolling back on any exception. There is no built-in equivalent in .NET, but the same behavior can be replicated with `DispatchProxy` and `TransactionScope`.

---

## How It Works

```
┌──────────────────────────────────────────────────────┐
│  DI Container                                        │
│                                                      │
│  IOrderService  ──►  TransactionProxy<IOrderService> │
│                            │                         │
│                            │  intercepts call        │
│                            ▼                         │
│                       [Transactional]?               │
│                       YES ──► TransactionScope       │
│                                    │                 │
│                                    ▼                 │
│                            OrderService.Method()     │
│                                    │                 │
│                         ┌──────────┴──────────┐      │
│                      success               exception │
│                         │                    │       │
│                     Complete()          Dispose()    │
│                       COMMIT            ROLLBACK     │
└──────────────────────────────────────────────────────┘
```

### Key components

| Component | Role |
|---|---|
| `[Transactional]` | Attribute that marks methods for transaction interception |
| `TransactionProxy<T>` | `DispatchProxy` subclass that intercepts every method call |
| `TransactionScopeExecutor` | Non-generic owner of commit/rollback/dispose logic and async wrappers |
| `TransactionScope` | Native .NET construct that manages the database transaction |
| `AddTransactionalServices()` | DI extension that auto-wires proxy registration |

### Why `TransactionScope` must be created **before** the method is called

```csharp
// WRONG — EF Core opens the connection before the scope is ambient
var task = InvokeTarget(method, args);   // DB connection opens here (no transaction)
using var scope = CreateScope(attr);     // Too late

// CORRECT — scope is ambient when EF Core opens its connection and enlists
var scope = CreateScope(attr);           // Ambient now
var task = (Task)InvokeTarget(method, args);  // Connection enlists in scope
return WrapAsync(task, scope);           // Wrapper calls Complete() or Dispose()
```

`TransactionScopeAsyncFlowOption.Enabled` propagates the ambient transaction through `ExecutionContext` across every `await` continuation inside the method.

---

## Solution Structure

```
src/
  Transactional.Core/               Pure library, no framework dependencies
    Attributes/
      TransactionalAttribute.cs     IsolationLevel, Propagation, RollbackFor, NoRollbackFor, TimeoutSeconds
    Observability/
      ITransactionLifecycleObserver.cs
      LoggingTransactionObserver.cs
      NullTransactionObserver.cs    Null Object singleton; eliminates null checks on the hot path
    Proxy/
      TransactionProxy.cs           DispatchProxy implementation — routing and caching only
      TransactionScopeExecutor.cs   All commit/rollback/dispose logic and async wrappers
      TransactionProxyFactory.cs    Generic and non-generic factory helpers
    Extensions/
      TransactionalExtensions.cs    AddTransactionalServices(Assembly) DI extension
  Transactional.Demo.Api/           ASP.NET Core Web API
    Entities/Order.cs
    Data/AppDbContext.cs            EF Core + SQLite
    Services/IOrderService.cs
    Services/OrderService.cs        [Transactional] service methods
    Services/IOrderFulfillmentService.cs
    Services/OrderFulfillmentService.cs  cross-service RequiresNew demo
    Controllers/OrdersController.cs
    Controllers/OrderFulfillmentController.cs
    Program.cs
tests/
  Transactional.Tests/
    Unit/TransactionProxyTests.cs        Proxy mechanics, no DB
    Integration/OrderServiceIntegrationTests.cs  Real SQLite commit/rollback
```

---

## `[Transactional]` attribute reference

All properties are optional. The attribute can be placed on the method in the concrete class or in the interface.

### `IsolationLevel`

Controls how the transaction is isolated from concurrent operations.

```csharp
[Transactional(IsolationLevel = IsolationLevel.Serializable)]
public async Task TransferFundsAsync(...) { }
```

| Value | Behaviour |
|---|---|
| `ReadCommitted` *(default)* | Reads only committed data; non-repeatable reads are possible |
| `ReadUncommitted` | Dirty reads allowed; lowest isolation |
| `RepeatableRead` | Rows read cannot change during the transaction; phantom reads are possible |
| `Serializable` | Full isolation; no dirty, non-repeatable, or phantom reads |
| `Snapshot` | Reads a consistent snapshot at the start of the transaction (requires DB support) |

### `Propagation`

Controls how the transaction behaves when a `TransactionScope` is already ambient (e.g., when one transactional method calls another).

```csharp
[Transactional(Propagation = TransactionScopeOption.RequiresNew)]
public async Task AuditLogAsync(...) { }
```

| Value | Behaviour |
|---|---|
| `Required` *(default)* | Join the ambient transaction if one exists; otherwise create a new one |
| `RequiresNew` | Always start a fresh, independent transaction, suspending any ambient one |
| `Suppress` | Execute outside any transaction, suspending the ambient one temporarily |

### `RollbackFor`

By default every exception triggers a rollback. `RollbackFor` narrows that to specific exception types (or their subclasses). All other exceptions will **commit** instead of rolling back.

```csharp
// Only roll back for domain exceptions; let validation errors commit
[Transactional(RollbackFor = [typeof(DomainException)])]
public async Task PlaceOrderAsync(...) { }
```

### `NoRollbackFor`

Commit the transaction even when one of the listed exception types is thrown. `NoRollbackFor` takes precedence over `RollbackFor` when a type appears in both.

```csharp
// Cancelled requests should not undo the transaction
[Transactional(NoRollbackFor = [typeof(OperationCanceledException)])]
public async Task ProcessAsync(CancellationToken ct) { }
```

### `TimeoutSeconds`

Abort and roll back the transaction if it exceeds the given number of seconds. `null` (default) uses the system-configured `TransactionManager.DefaultTimeout`.

```csharp
[Transactional(TimeoutSeconds = 30)]
public async Task LongRunningImportAsync(...) { }
```

### Combining properties

```csharp
[Transactional(
    IsolationLevel   = IsolationLevel.RepeatableRead,
    Propagation      = TransactionScopeOption.RequiresNew,
    TimeoutSeconds   = 60,
    NoRollbackFor    = [typeof(OperationCanceledException)])]
public async Task CriticalOperationAsync(CancellationToken ct) { }
```

---

## Running the API

```bash
cd src/Transactional.Demo.Api
dotnet run
```

Swagger UI: `http://localhost:51938/swagger`

| Endpoint | What happens |
|---|---|
| `POST /orders/success` | Order is inserted and committed — returns 201 |
| `POST /orders/fail` | Order is inserted then an exception is thrown — transaction rolls back, returns 400 |
| `GET /orders` | Returns all persisted orders |
| `POST /fulfillment/fulfill` | Outer Required scope calls inner RequiresNew — both commit |
| `POST /fulfillment/fulfill-then-fail` | Inner RequiresNew commits independently before outer Required fails — inner data persists |

### Verify commit and rollback manually

```bash
# Commit — order persisted
curl -X POST http://localhost:51938/orders/success
curl http://localhost:51938/orders        # → [{id:1,...}]

# Rollback — nothing persisted
curl -X POST http://localhost:51938/orders/fail
curl http://localhost:51938/orders        # → still [{id:1,...}], not 2 items
```

---

## Running the Tests

```bash
dotnet test
```

| Test | What it proves |
|---|---|
| `CreateSuccess_CommitsOrder_*` | `[Transactional]` method commits on success |
| `CreateWithRollback_RollsBack_*` | Exception causes rollback — nothing in DB |
| `TwoSuccessfulCalls_BothOrdersPersisted` | Each call gets its own transaction |
| `SuccessThenRollback_OnlyFirstOrderPersisted` | Rollback is isolated — prior commits survive |
| `Method_WithoutAttribute_PassesThrough` | Proxy is transparent for non-decorated methods |
| `AfterCommit_OnSuccess_AsyncHookFires` | Async hook fires after commit |
| `AfterCommit_OnRollback_HookNotFired` | Hook is discarded on rollback |
| `AfterCommit_SyncHook_FiresInAsyncMethod` | Sync `Action` hook works in async method |
| `AfterCommit_WhenOneHookFails_RemainingHooksStillFire` | All hooks run even if one throws — `AggregateException` |

---

## Post-Commit Hooks

`ITransactionHooks` lets you schedule callbacks that run **after the database confirms the commit**. Hooks are silently discarded on rollback.

Inject `ITransactionHooks` into any service that needs post-commit side effects:

```csharp
public class OrderService : IOrderService
{
    private readonly AppDbContext _db;
    private readonly ITransactionHooks _hooks;

    public OrderService(AppDbContext db, ITransactionHooks hooks)
    {
        _db    = db;
        _hooks = hooks;
    }

    [Transactional]
    public async Task<Order> CreateAsync()
    {
        var order = new Order { CreatedAt = DateTime.UtcNow };
        _db.Orders.Add(order);
        await _db.SaveChangesAsync();

        // Fires only if the transaction commits — never on rollback.
        _hooks.AfterCommit(async () => await _bus.PublishAsync(new OrderCreated(order.Id)));

        return order;
    }
}
```

### Execution order

All synchronous (`Action`) hooks run first, then all asynchronous (`Func<Task>`) hooks — in the order each group was registered. If strict cross-type ordering is needed, combine both into a single `Func<Task>`.

### Error isolation

Every hook executes even if a previous one throws. Exceptions are collected and rethrown as a single `AggregateException` after all hooks have run, so no side effect is silently suppressed.

### Sync `[Transactional]` methods

`Action` hooks work on synchronous methods. `Func<Task>` hooks throw `NotSupportedException` because they cannot be awaited on a synchronous call path — change the method return type to `Task` or `Task<T>`.

### DI registration

`AddTransactionalServices()` registers `ITransactionHooks` automatically as a singleton. No manual wiring is needed.

---

## Database Compatibility

The proxy relies on `System.Transactions.TransactionScope` to create an ambient transaction. Whether that transaction is enforced at the database level depends on the EF Core provider.

| Database | EF Core provider | `System.Transactions` support | Works with the proxy? |
|---|---|---|---|
| SQL Server | `SqlServer` | Full enlistment | Yes |
| PostgreSQL | `Npgsql` | Full enlistment | Yes |
| MySQL | `Pomelo.EntityFrameworkCore.MySql` | Local transactions only | Yes (local); distributed (MSDTC) not supported by MySQL |
| SQLite | `Sqlite` | None (`SupportsAmbientTransactions = false`) | Proxy lifecycle is correct; the database ignores the scope |
| MongoDB | .NET Driver | None — uses `IClientSessionHandle` | No — requires a different strategy |
| CosmosDB | `EF Cosmos` | None — ACID per-item only | No — no multi-item transaction support |

**MongoDB and CosmosDB** do not enlist in `System.Transactions` at all. Wrapping their operations in a `TransactionScope` has no effect. A proper integration would require a strategy abstraction (e.g. `ITransactionStrategy`) that delegates to `IClientSessionHandle` for MongoDB or relies on optimistic concurrency primitives for CosmosDB.

---

## Design Patterns

### Proxy (Structural)

`TransactionProxy<T>` is the pattern in its entirety. The proxy and the real object (`_target`) share the same interface `T`; callers never hold a reference to the concrete class. Methods without `[Transactional]` are forwarded transparently via a compiled delegate. Decorated methods receive transactional behaviour without the caller being aware.

### Template Method (Behavioral)

Every execution path follows the same skeleton:

```
OpenScope → Invoke → Commit (success) | Rollback (eligible exception) | Commit (ineligible exception) → Dispose
```

`WrapVoidTaskAsync` is the canonical implementation of this skeleton for async paths. `WrapGenericTaskAsync<TResult>` and `WrapGenericValueTaskAsync<TResult>` follow the same structure and extract the result at the end. `HandleSync` reimplements the skeleton synchronously — the structural duplication is intentional: mixing `async` code with synchronous execution corrupts `Transaction.Current` after the scope is disposed (confirmed by the `RequiresNew_InsideAmbientScope_SuspendsAndRestoresOuterTransaction` test).

### Observer (Behavioral)

`ITransactionLifecycleObserver` decouples event notification (`OnBegin`, `OnCommit`, `OnRollback`) from transaction logic. `LoggingTransactionObserver` is the default concrete observer. Custom implementations can feed metrics systems, distributed tracing, or test assertions without touching service code.

### Strategy (Behavioral)

`ShouldRollback` encapsulates the rollback decision rules parameterised by `TransactionalAttribute`. Each method can carry a different strategy via its attribute. The three strategies — `NoRollbackFor` wins, `RollbackFor` restricts, default rolls back on any exception — are stable and do not warrant extraction into an interface hierarchy.

### Null Object (Behavioral)

`NullTransactionObserver` is an `internal` singleton with no-op implementations of all three observer methods. `TransactionProxy<T>` uses `observer ?? NullTransactionObserver.Instance` at initialisation, making the `_observer` field non-nullable and eliminating null-checks on the hot path — the same pattern used by the BCL in `Stream.Null` and `TextWriter.Null`.

---

## Key Concepts

### `DispatchProxy`

A built-in .NET class that generates a runtime proxy implementing any interface. You override `Invoke()` to intercept every method call. This is how the framework adds behavior (transaction management) without touching the real class.

### `TransactionScope`

`System.Transactions.TransactionScope` creates an ambient transaction. Any database connection opened while the scope is active will automatically enlist in it. When `scope.Complete()` is called before `Dispose()`, the transaction commits; otherwise it rolls back.

### `async` support

The `Invoke()` method is synchronous, but the intercepted methods are `async`. The trick:
1. Create the `TransactionScope` synchronously (so it is ambient)
2. Invoke the method (it starts and returns a `Task` or `ValueTask`)
3. Return a **new** task that `await`s the original one and then calls `Complete()` or `Dispose()`

For `Task<T>` and `ValueTask<T>`, where the result type is unknown at compile time, a private generic helper (`WrapGenericTaskAsync<TResult>` / `WrapGenericValueTaskAsync<TResult>`) is invoked via `MethodInfo.MakeGenericMethod` at runtime to capture and forward the return value. Both wrappers `await` the original task or `ValueTask` directly — no `.AsTask()` allocation is needed.

### Performance caches

`TransactionProxy<T>` maintains two static `ConcurrentDictionary` caches shared across all proxy instances of the same interface:
- **Attribute cache** — `(MethodInfo, Type) → TransactionalAttribute?` avoids repeated reflection scans. The key includes the concrete type to support multiple implementations of the same interface. On the first call to a method the cache checks the interface `MethodInfo` first; if no attribute is found there it resolves the concrete counterpart via `GetInterfaceMap` and checks that. This means `[Transactional]` can be placed on either the interface or the concrete class — prefer the concrete class. Subsequent calls pay no reflection cost.
- **Delegate cache** — `MethodInfo → compiled Func<...>` replaces `MethodInfo.Invoke` with a compiled Expression tree on the hot path

### Selective rollback

C#'s exception filter (`when`) makes selective rollback possible without catching and re-throwing. All async wrappers use a nested try structure to guarantee that `Commit` on the success path is never inside any catch block:

```csharp
try
{
    try
    {
        result = await task.ConfigureAwait(false);
    }
    catch (Exception ex) when (ShouldRollback(ctx.Attr, ex))
    {
        Rollback(ctx, ex);   // rolls back and propagates
        throw;
    }
    catch (Exception)
    {
        Commit(ctx);         // NoRollbackFor path — commit despite exception, then propagate
        throw;
    }
    Commit(ctx);             // success path — outside all catch scopes, no risk of double Complete()
}
finally
{
    ctx.Scope.Dispose();
}
```

The filter runs *before* the stack unwinds. If `ShouldRollback` returns `true`, the first handler fires and rolls back. If `ShouldRollback` returns `false` (e.g. the exception matches `NoRollbackFor`), the filter evaluates to `false`, the first handler is skipped, and the second handler commits. Placing `Commit` on the success path *outside* the inner `catch` blocks prevents double `scope.Complete()` if the observer itself throws.

### Observability

`ITransactionLifecycleObserver` provides a hook into every transaction event:

```csharp
public interface ITransactionLifecycleObserver
{
    void OnBegin(MethodInfo method, TransactionalAttribute attr);
    void OnCommit(MethodInfo method, TimeSpan elapsed);
    void OnRollback(MethodInfo method, Exception exception, TimeSpan elapsed);
}
```

The built-in `LoggingTransactionObserver` writes structured log entries at `DEBUG` (BEGIN/COMMIT) and `WARNING` (ROLLBACK) level. Register it by calling `services.AddTransactionalLogging()` in `Program.cs`. The `OnRollback` entry includes `{ExceptionType}` and `{ExceptionMessage}` as first-class structured properties, independently filterable in Serilog, OpenTelemetry, and similar stores. Custom implementations can feed metrics systems, distributed tracing, or test assertions without touching service code.

### DI auto-discovery convention

`AddTransactionalServices(Assembly)` scans for concrete classes that have at least one `[Transactional]` method (on the class or on its implemented interfaces) and a matching interface named `I{ClassName}` in the same assembly. Both the concrete type and the interface are registered as `Scoped`. The interface resolves as a `TransactionProxy` wrapping the concrete instance; the concrete type is also resolvable directly from the container, so any new constructor dependency it gains is handled automatically by DI without changing the registration.
