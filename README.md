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
| `TransactionScopeExecutor` | Owns all commit/rollback/dispose logic and async wrappers |
| `TransactionScope` | Native .NET construct that manages the database transaction |
| `ITransactionHooks` | Per-scope callback registry — schedule side effects after commit, rollback, or completion |
| `ITransactionLifecycleObserver` | Observer interface for begin/commit/rollback events |
| `AddTransactionalServices()` | DI extension that auto-wires proxy registration |

### Why `TransactionScope` must be created **before** the method is called

```csharp
// WRONG — EF Core opens the connection before the scope is ambient
var task = service.MethodAsync();            // DB connection opens here (no transaction)
using var scope = new TransactionScope();    // Too late — connection already enrolled outside scope

// CORRECT — scope is ambient when EF Core opens its connection and enlists
using var scope = new TransactionScope();    // Ambient now
var task = service.MethodAsync();            // Connection enlists in scope
await task;                                  // Scope committed or rolled back after method completes
```

`TransactionScopeAsyncFlowOption.Enabled` propagates the ambient transaction through `ExecutionContext` across every `await` continuation inside the method.

---

## Solution Structure

```
src/
  Transactional.Core/               Pure library, no framework dependencies
    Attributes/
      TransactionalAttribute.cs     IsolationLevel, Propagation, RollbackFor, NoRollbackFor, TimeoutSeconds
    Hooks/
      ITransactionHooks.cs          Public interface — AfterCommit, AfterRollback, AfterCompletion
      TransactionHooks.cs           AsyncLocal-backed implementation; manages per-scope HookCollection
    Observability/
      ITransactionLifecycleObserver.cs
      LoggingTransactionObserver.cs
      NullTransactionObserver.cs    Null Object singleton
    Proxy/
      TransactionProxy.cs           DispatchProxy subclass — routing and return-type dispatch
      TransactionScopeExecutor.cs   All commit/rollback/dispose logic and async wrappers
      TransactionProxyFactory.cs
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
    Unit/
      ProxyMechanicsTests.cs     Proxy routing, attribute lookup, return-type dispatch
      PropagationTests.cs        Required / RequiresNew / Suppress scope behaviour
      RollbackRulesTests.cs      ShouldRollback precedence — NoRollbackFor, RollbackFor, default
      ObserverTests.cs           ITransactionLifecycleObserver event sequencing
    Integration/
      OrderServiceIntegrationTests.cs    Real SQLite commit/rollback
      Hooks/
        AfterCommitTests.cs              AfterCommit sync/async hooks, AggregateException guarantee
        AfterRollbackTests.cs            AfterRollback hooks, ValueTask path, error isolation
        AfterCompletionTests.cs          AfterCompletion fires on both commit and rollback paths
        HookScopeTests.cs                Nested scope isolation (RequiresNew, Suppress, Required joining)
        HookErrorTests.cs                Error-path coverage — NoRollbackFor, observer throws, Dispose throws
        SyncPathHookTests.cs             Sync [Transactional] methods — Action hooks, async-hook guard
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

| File | What it covers |
|---|---|
| `ProxyMechanicsTests.cs` | Proxy routing, attribute lookup on interface and concrete class, all return types (`Task`, `Task<T>`, `ValueTask`, `ValueTask<T>`, sync), factory validation |
| `PropagationTests.cs` | `Required`, `RequiresNew`, and `Suppress` scope behaviour; ambient transaction isolation |
| `RollbackRulesTests.cs` | `RollbackFor`, `NoRollbackFor`, default-all precedence rules |
| `ObserverTests.cs` | `ITransactionLifecycleObserver` event sequencing — begin, commit, rollback |
| `OrderServiceIntegrationTests.cs` | End-to-end commit and rollback against a real SQLite database; `RequiresNew` cross-service isolation; `NoRollbackFor` with real persistence |
| `AfterCommitTests.cs` | `AfterCommit` fires after commit and not on rollback; sync and async hooks; `AggregateException` when a hook throws; no-op outside a scope |
| `AfterRollbackTests.cs` | `AfterRollback` fires after rollback and not on commit; `ValueTask` path; all hooks run even if one throws |
| `AfterCompletionTests.cs` | `AfterCompletion` fires on both commit and rollback; hook ordering relative to `AfterCommit`; `AggregateException` on failure |
| `HookScopeTests.cs` | Hook isolation across `RequiresNew`, `Suppress`, and joining `Required` scopes; nested scope stack correctness |
| `HookErrorTests.cs` | `NoRollbackFor` path does not fire `AfterRollback`; hook failure does not mask original exception; hooks still fire when `Scope.Dispose()` throws |
| `SyncPathHookTests.cs` | Sync `[Transactional]` methods: `Action` hooks work, `Func<Task>` hooks throw `NotSupportedException` before any hook executes |

---

## Transaction Lifecycle Hooks

`ITransactionHooks` lets you register callbacks that fire at the end of a transaction scope. Three events are available:

| Method | Fires when |
|---|---|
| `AfterCommit` | Transaction committed successfully |
| `AfterRollback` | Transaction rolled back (exception or explicit `Rollback()`) |
| `AfterCompletion` | Transaction completed in any way — commit **or** rollback |

Each method accepts either a synchronous `Action` or an asynchronous `Func<Task>`:

```csharp
_hooks.AfterCommit(() => cache.Invalidate(orderId));          // sync
_hooks.AfterCommit(async () => await bus.PublishAsync(...));  // async

_hooks.AfterRollback(() => logger.Warn("order rolled back"));

_hooks.AfterCompletion(() => metrics.RecordTransactionEnd());
```

Inject `ITransactionHooks` into any service that needs lifecycle side effects:

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

        // Fires only after the transaction commits — never on rollback.
        _hooks.AfterCommit(async () => await _bus.PublishAsync(new OrderCreated(order.Id)));

        // Fires regardless of outcome — useful for releasing resources.
        _hooks.AfterCompletion(() => _span.Finish());

        return order;
    }
}
```

### Hook semantics by outcome

| Outcome | `AfterCommit` | `AfterRollback` | `AfterCompletion` |
|---|---|---|---|
| Committed | fires | — | fires |
| Rolled back | — | fires | fires |
| Committed (NoRollbackFor path) | fires | — | fires |

### Execution order

All synchronous (`Action`) hooks run first, then all asynchronous (`Func<Task>`) hooks — in the order each group was registered. If strict cross-type ordering is needed, combine both into a single `Func<Task>`.

### Error isolation

Every hook executes even if a previous one throws. Exceptions are collected and rethrown as a single `AggregateException` after all hooks have run, so no side effect is silently suppressed.

On rollback and `NoRollbackFor` paths — where an exception is already propagating — hook failures are suppressed so they do not mask the original exception.

### Nested scopes

Hook registration follows the scope's `Propagation` setting:

- **Required (joining)** — hooks flow into the ambient (outer) collection and fire when the outer scope commits or rolls back.
- **RequiresNew** — hooks are scoped to the new independent transaction and fire when it completes.
- **Suppress** — hook registrations inside the suppressed region are no-ops. The outer scope's hooks are restored automatically when the suppressed scope exits.

### Sync `[Transactional]` methods

`Action` hooks work on synchronous methods. `Func<Task>` hooks throw `NotSupportedException` because they cannot be awaited on a synchronous call path — the guard fires before any hook executes, so no partial side effects occur. Change the method return type to `Task` or `Task<T>` to use async hooks.

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

`TransactionProxy<T>` is the pattern in its entirety. The proxy and the real object share the same interface `T`; callers never hold a reference to the concrete class. Methods without `[Transactional]` are forwarded transparently. Decorated methods receive transactional behaviour without the caller being aware.

### Template Method (Behavioral)

Every execution path follows the same skeleton:

```
OpenScope → Invoke → Commit (success) | Rollback (eligible exception) | Commit (ineligible exception) → Dispose
```

`WrapVoidTaskAsync` is the canonical async implementation; `WrapGenericTaskAsync<TResult>`, `WrapVoidValueTaskAsync`, and `WrapGenericValueTaskAsync<TResult>` follow the same structure. `HandleSync` reimplements it synchronously — mixing `async` code with synchronous `TransactionScope` management corrupts `Transaction.Current` after `Dispose()`.

### Observer (Behavioral)

`ITransactionLifecycleObserver` decouples event notification (`OnBegin`, `OnCommit`, `OnRollback`) from transaction logic. `LoggingTransactionObserver` is the default concrete observer. Custom implementations can feed metrics systems, distributed tracing, or test assertions without touching service code.

### Strategy (Behavioral)

The rollback decision is parameterised entirely by `[Transactional]`'s `NoRollbackFor`, `RollbackFor`, and default-all properties. Each method carries its own strategy via its attribute — three fixed rules evaluated in precedence order: `NoRollbackFor` wins, `RollbackFor` restricts, default rolls back on any exception.

### Null Object (Behavioral)

`NullTransactionObserver` is a singleton with no-op implementations of all observer methods. Passing `null` to `TransactionProxyFactory.Create` is valid — the proxy substitutes it automatically, making the observer field non-nullable and eliminating null-checks on the hot path. Same pattern as `Stream.Null` and `TextWriter.Null` in the BCL.

---

## Key Concepts

### `DispatchProxy`

A built-in .NET class that generates a runtime proxy implementing any interface. You override `Invoke()` to intercept every method call. This is how the framework adds behavior (transaction management) without touching the real class.

### `TransactionScope`

`System.Transactions.TransactionScope` creates an ambient transaction. Any database connection opened while the scope is active will automatically enlist in it. When `scope.Complete()` is called before `Dispose()`, the transaction commits; otherwise it rolls back.

### `async` support

`DispatchProxy.Invoke()` is synchronous, but the intercepted methods are `async`. The trick:
1. Create the `TransactionScope` synchronously (so it is ambient before the method starts)
2. Invoke the method — it begins executing and returns a `Task` or `ValueTask`
3. Return a **new** task that `await`s the original one and then calls `Complete()` or `Dispose()`

For `Task<T>` and `ValueTask<T>`, where the result type is unknown at compile time, a generic wrapper is invoked via `MakeGenericMethod` to capture and forward the return value.

### Performance caches

`TransactionProxy<T>` maintains two static caches per interface:
- **Attribute cache** — avoids repeated reflection scans; checks the interface method first, then the concrete counterpart via `GetInterfaceMap`. `[Transactional]` can be placed on either the interface or the concrete class.
- **Delegate cache** — replaces `MethodInfo.Invoke` with a compiled Expression tree on the hot path.

### Selective rollback

C#'s exception filter (`when`) makes selective rollback possible without catching and re-throwing. Wrappers use a nested try structure so that `scope.Complete()` on the success path is never inside any catch block:

```csharp
try
{
    try
    {
        result = await task;
    }
    catch (Exception ex) when (ShouldRollback(attr, ex))
    {
        Rollback();  // rolls back, then propagates
        throw;
    }
    catch (Exception)
    {
        scope.Complete(); // NoRollbackFor path — commit despite exception, then propagate
        throw;
    }
    scope.Complete();     // success path — outside all catch blocks, no risk of double-Complete
}
finally
{
    scope.Dispose();
}
```

The filter runs before the stack unwinds. If `ShouldRollback` returns `false` (e.g. `NoRollbackFor` matches), the first handler is skipped and the second commits. `Complete()` on the success path is intentionally outside all catch blocks — this prevents a double-complete if the observer throws during the commit notification.

### Observability

`ITransactionLifecycleObserver` receives a notification for every transaction event — begin, commit, and rollback — including elapsed time and, on rollback, the exception that caused it. Implement it to feed metrics, distributed tracing, or structured logging without touching service code.

The built-in `LoggingTransactionObserver` writes structured log entries at `DEBUG` (BEGIN/COMMIT) and `WARNING` (ROLLBACK) level. Register it by calling `services.AddTransactionalLogging()` in `Program.cs`. The rollback entry includes `{ExceptionType}` and `{ExceptionMessage}` as first-class structured properties, independently filterable in Serilog, OpenTelemetry, and similar stores.

### DI auto-discovery convention

`AddTransactionalServices(Assembly)` discovers all concrete classes that have at least one `[Transactional]` method and a matching interface named `I{ClassName}` in the same assembly. Both the concrete type and the interface are registered as `Scoped`. Resolve the interface to get the proxy; the concrete type is also resolvable directly, so constructor dependencies are handled by DI without manual registration changes.
