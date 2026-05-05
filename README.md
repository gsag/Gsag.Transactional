# csharp-transactional-pattern

[![CI](https://github.com/gsag/csharp-transactional-pattern/actions/workflows/ci.yml/badge.svg)](https://github.com/gsag/csharp-transactional-pattern/actions/workflows/ci.yml) ![.NET](https://img.shields.io/badge/.NET-8%20%7C%209-512BD4?logo=dotnet) ![License](https://img.shields.io/badge/license-MIT-green) ![Status](https://img.shields.io/badge/status-experimental-orange)

Declarative `[Transactional]` attribute for C# using **only native .NET** — `DispatchProxy` + `TransactionScope`. No AOP libraries.

Inspired by Spring Framework's `@Transactional`, this project brings the same declarative model to .NET. Instead of wrapping every method in `try/catch/TransactionScope.Complete()`, you annotate it once and let the proxy handle commit, rollback, and lifecycle hooks — keeping business logic free of transaction plumbing.

> **⚠️ Experimental project** — built for learning and exploring patterns. Not recommended for production use.

---

## How it works

```
Controller → IMyService (proxy) → TransactionProxy
                                           │
                                     [Transactional]?
                                     YES → TransactionScope
                                                │
                                        MyService.Method()
                                                │
                                      ┌─────────┴─────────┐
                                   success            exception
                                      │                   │
                                 Complete()          Dispose()
                                  COMMIT             ROLLBACK
```

---

## Quick Start

**1. Reference `Transactional.Core`** in your project.

**2. Mark methods with `[Transactional]`:**

```csharp
public class MyService : IMyService
{
    private readonly DbContext _db;

    public MyService(DbContext db) => _db = db;

    [Transactional]
    public async Task<Entity> CreateAsync(string name)
    {
        var entity = new Entity { Name = name };
        _db.Entities.Add(entity);
        await _db.SaveChangesAsync();
        return entity;
    }
}
```

**3. Register in DI:**

```csharp
// Program.cs
builder.Services.AddTransactionalServices(typeof(MyService).Assembly);
```

**4. Inject and use the interface — the proxy is transparent:**

```csharp
public class MyController : ControllerBase
{
    private readonly IMyService _service_;

    public MyController(IMyService service) => _service = service;

    [HttpPost]
    public async Task<IActionResult> Create(string name)
        => Ok(await _service.CreateAsync(name));
}
```

`AddTransactionalServices` auto-discovers every concrete class with at least one `[Transactional]` method and a matching interface named `I{ClassName}` in the same assembly.

---

## `[Transactional]` reference

| Property | Type | Default | Description |
|---|---|---|---|
| `IsolationLevel` | `IsolationLevel` | `ReadCommitted` | Transaction isolation level |
| `Propagation` | `TransactionScopeOption` | `Required` | Behaviour when a transaction is already active |
| `RollbackFor` | `Type[]` | — | Roll back only for these exception types; commit for all others |
| `NoRollbackFor` | `Type[]` | — | Commit even when these types are thrown (takes precedence over `RollbackFor`) |
| `TimeoutSeconds` | `int?` | `null` | Abort and roll back after N seconds |

### IsolationLevel

Controls how the database isolates concurrent transactions from each other. Higher isolation levels prevent more anomalies but increase contention.

| Value | Dirty reads | Non-repeatable reads | Phantom reads | Notes |
|---|---|---|---|---|
| `ReadUncommitted` | possible | possible | possible | Lowest isolation; reads uncommitted data from other transactions |
| `ReadCommitted` *(default)* | prevented | possible | possible | Only reads data committed by other transactions |
| `RepeatableRead` | prevented | prevented | possible | Rows read cannot be modified by others during the transaction |
| `Serializable` | prevented | prevented | prevented | Full isolation; transactions execute as if sequential |
| `Snapshot` | prevented | prevented | prevented | Reads a consistent snapshot from transaction start; requires DB support |
| `Chaos` | — | — | — | Pending changes from higher-isolation transactions cannot be overwritten |
| `Unspecified` | — | — | — | Uses the isolation level of the underlying provider |

> See [IsolationLevel Enum](https://learn.microsoft.com/en-us/dotnet/api/system.transactions.isolationlevel) on Microsoft Learn for the full specification.

### Propagation modes

Controls how a `[Transactional]` method behaves when a `TransactionScope` is already ambient — for example, when one transactional method calls another.

| Value | Ambient transaction exists | No ambient transaction | Use when |
|---|---|---|---|
| `Required` *(default)* | Join the existing scope | Create a new scope | Most business operations — share the caller's unit of work |
| `RequiresNew` | Suspend the outer scope; open a new independent one | Create a new scope | Audit logs, outbox records — must commit even if the caller rolls back |
| `Suppress` | Suspend the outer scope; run without any transaction | Run without any transaction | Read-only queries, external calls that must not enlist in a transaction |

> See [TransactionScopeOption Enum](https://learn.microsoft.com/en-us/dotnet/api/system.transactions.transactionscopeoption) on Microsoft Learn for the full specification.

### Examples

```csharp
// Custom isolation and timeout
[Transactional(IsolationLevel = IsolationLevel.Serializable, TimeoutSeconds = 30)]
public async Task TransferFundsAsync(...) { }

// Roll back only on domain exceptions; commit on validation errors
[Transactional(RollbackFor = [typeof(DomainException)])]
public async Task PlaceOrderAsync(...) { }

// Always commit even when cancelled
[Transactional(NoRollbackFor = [typeof(OperationCanceledException)])]
public async Task ProcessAsync(CancellationToken ct) { }

// Independent nested transaction
[Transactional(Propagation = TransactionScopeOption.RequiresNew)]
public async Task AuditLogAsync(...) { }
```

---

## Transaction Lifecycle Hooks

`ITransactionHooks` lets you schedule side effects to run after the transaction commits, rolls back, or completes — without touching the transaction logic itself.

```csharp
public class MyService : IMyService
{
    private readonly DbContext _db;
    private readonly ITransactionHooks _hooks;

    public MyService(DbContext db, ITransactionHooks hooks)
    {
        _db    = db;
        _hooks = hooks;
    }

    [Transactional]
    public async Task<Entity> CreateAsync()
    {
        var entity = new Entity { CreatedAt = DateTime.UtcNow };
        _db.Entities.Add(entity);
        await _db.SaveChangesAsync();

        // Fires only after the transaction commits.
        _hooks.AfterCommit(async () => await _bus.PublishAsync(new EntityCreated(entity.Id)));

        // Fires regardless of outcome — useful for releasing resources.
        _hooks.AfterCompletion(() => _span.Finish());

        return entity;
    }
}
```

| Method | Fires when |
|---|---|
| `AfterCommit` | Transaction committed successfully |
| `AfterRollback` | Transaction rolled back |
| `AfterCompletion` | Transaction completed in any way — commit or rollback |

Each method accepts a synchronous `Action` or an asynchronous `Func<Task>`. `ITransactionHooks` is registered automatically by `AddTransactionalServices()`.

---

## Running locally

```bash
# Build
dotnet build

# Run all tests
dotnet test

# Run a specific test class
dotnet test --filter "FullyQualifiedName~CheckoutIntegrationTests"

# Run the demo API — opens Swagger automatically in the browser
# http://localhost:51938/swagger  (HTTPS: https://localhost:51937/swagger)
dotnet run --project src/Transactional.Demo.Api
```

### Demo API — E-Commerce Checkout

Each endpoint demonstrates one specific `[Transactional]` behaviour. Every response includes `hooksOutput` (registered hooks and their execution order) and `publishedEvents` (events dispatched after commit) so you can observe the library's behaviour directly in the response body.

| Endpoint | `[Transactional]` config | What it demonstrates |
|---|---|---|
| `POST /checkout/success` | `Required` | Full commit — OrderService, InventoryService, PaymentService join the outer scope; AuditService commits independently via `RequiresNew`; AfterCommit hooks fire at the outer commit |
| `POST /checkout/payment-failure` | `Required` | Rollback — PaymentService throws before `SaveChanges`; outer scope disposes without `Complete()`; AfterRollback hooks run |
| `POST /checkout/inventory-failure` | `Required` | Rollback — InventoryService throws before `SaveChanges`; identical lifecycle to payment failure |
| `POST /checkout/audit-requires-new` | `Required` outer + `RequiresNew` audit | AuditEntry commits independently; outer scope then throws and rolls back — audit row survives |
| `POST /checkout/no-rollback-for` | `NoRollbackFor=[NotificationException]` | `NotificationException` triggers `scope.Complete()` instead of rollback — order and payment persist despite the 400 response |
| `POST /checkout/after-commit-hook` | `Required` | AfterCommit hook publishes `payment.approved` to the event bus **only after** `scope.Complete()`; hook does not fire when the inner method returns |
| `POST /checkout/after-rollback-hook` | `Required` | Three compensating hooks (2 sync + 1 async) registered and executed in order after rollback |
| `POST /checkout/suppress` | `Required` outer + `Suppress` read | InventoryReportService runs with `Transaction.Current = null`; outer scope is suspended for its duration and automatically resumed |
| `GET /checkout/orders` | — | All persisted checkout orders |
| `GET /checkout/audit-log` | — | All persisted audit entries |
| `GET /checkout/payments` | — | All persisted payment records |
| `DELETE /checkout/reset` | — | Clears all data between demo runs |

---

## Known limitations

- **Self-invocation bypasses the proxy** — calling `this.Method()` inside the same class skips `DispatchProxy`. Always inject the interface so calls route through the proxy.
- **SQLite has no ambient transaction support** — EF Core 9's SQLite provider does not enlist in `System.Transactions`. The proxy lifecycle works correctly; the database itself ignores the scope. Use SQL Server or PostgreSQL for real transactional guarantees.

---

## Project structure

```
src/
  Transactional.Core/          Pure library — no framework dependencies
    Attributes/                [Transactional] attribute
    Hooks/                     ITransactionHooks (AfterCommit, AfterRollback, AfterCompletion)
    Observability/             ITransactionLifecycleObserver, NullTransactionObserver
    Proxy/                     TransactionProxy<T>, TransactionScopeExecutor, TransactionProxyFactory
    Extensions/                AddTransactionalServices() DI extension
  Transactional.Demo.Api/      ASP.NET Core + EF Core + SQLite — e-commerce checkout demo
    Entities/                  CheckoutOrder, InventoryReservation, PaymentRecord, AuditEntry
    Data/                      CheckoutDbContext
    Exceptions/                PaymentDeclinedException, InventoryException, NotificationException
    Infrastructure/            HookOutputCollector, InMemoryEventBus (both Scoped per-request)
    Services/                  OrderService, InventoryService, PaymentService, AuditService,
                               InventoryReportService, CheckoutService (8 scenario methods)
    Controllers/               CheckoutController (8 POST + 4 GET/DELETE)
tests/
  Transactional.Tests/
    Unit/                      Proxy mechanics, propagation, rollback rules, observer
    Integration/               CheckoutIntegrationTests — real SQLite, full service graph
    Integration/Hooks/         AfterCommit, AfterRollback, AfterCompletion, nested scopes
```

---

## License

MIT
