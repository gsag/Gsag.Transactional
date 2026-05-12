# Gsag.Transactional

[![CI](https://github.com/gsag/Gsag.Transactional/actions/workflows/ci.yml/badge.svg)](https://github.com/gsag/Gsag.Transactional/actions/workflows/ci.yml) ![.NET](https://img.shields.io/badge/.NET-8%20%7C%209-512BD4?logo=dotnet) [![License](https://img.shields.io/badge/license-MIT-green)](LICENSE) ![Status](https://img.shields.io/badge/status-pre--release-yellow)

Lightweight declarative `[Transactional]` attribute for C# using **only native .NET** — `DispatchProxy` + `TransactionScope`. No AOP libraries.

Inspired by Spring Framework's `@Transactional`, this project brings the same declarative model to .NET. Instead of wrapping every method in `try/catch/TransactionScope.Complete()`, you annotate it once and let the proxy handle commit, rollback, and lifecycle hooks — keeping business logic free of transaction plumbing.

> **⚠️ In stabilization — the public API may change between releases. Not recommended for production use yet.**

---

## Contents

- [How it works](#how-it-works)
- [Quick Start](#quick-start)
- [`[Transactional]` reference](#transactional-reference)
- [Transaction Lifecycle Hooks](#transaction-lifecycle-hooks)
- [Transaction Observer](#transaction-observer)
- [Running locally](#running-locally)
- [Known limitations](#known-limitations)
- [Project structure](#project-structure)
- [License](LICENSE)

---

## How it works

```
      Controller
           │
           ▼
   IMyService  (proxy)
           │
           ▼
    [Transactional]
           │
           ▼
    TransactionScope
           │
           ▼
    MyService.Method()
           │
     ┌─────┴─────┐
     │           │
  success    exception
     │           │
 Complete()  Dispose()
   COMMIT    ROLLBACK
```

---

## Quick Start

**1. Reference `Gsag.Transactional.Core`** in your project.

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
    private readonly IMyService _service;

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

`ITransactionHooks` lets you schedule side effects at every point in the transaction lifecycle — before and after commit or rollback — without touching the transaction logic itself.

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

        // Fires inside the scope, before scope.Complete(). If this throws, the
        // transaction rolls back and AfterRollback fires instead of AfterCommit.
        _hooks.BeforeCommit(async () => await _validator.ValidateFinalStateAsync());

        // Fires only after the transaction commits.
        _hooks.AfterCommit(async () => await _bus.PublishAsync(new EntityCreated(entity.Id)));

        // Fires regardless of outcome — useful for releasing resources.
        _hooks.AfterCompletion(() => _span.Finish());

        return entity;
    }
}
```

| Method | When it fires | Inside scope? | If the hook throws |
|---|---|---|---|
| `BeforeCommit` | Just before `scope.Complete()` — success and `NoRollbackFor` paths | Yes | Success path: transaction rolls back, `AfterRollback` fires. `NoRollbackFor` path: exception suppressed, original exception propagates. |
| `AfterCommit` | After the transaction commits | No | Propagates to the caller |
| `BeforeRollback` | Just before `scope.Dispose()` on the rollback path | Yes | Suppressed — original rollback exception always propagates |
| `AfterRollback` | After the transaction rolls back | No | Suppressed |
| `AfterCompletion` | After the transaction resolves, commit or rollback | No | Suppressed on rollback and `NoRollbackFor` paths |

Each method accepts a synchronous `Action` or an asynchronous `Func<Task>`. Async overloads (`Func<Task>`) throw `NotSupportedException` when registered inside a synchronous `[Transactional]` method — change the method return type to `Task` to use them. `ITransactionHooks` is registered automatically by `AddTransactionalServices()`.

---

## Transaction Observer

`ITransactionObserver` lets infrastructure components (logging, metrics, tracing) observe every transaction without coupling to business logic. Register one or more implementations in DI — the proxy calls all of them in registration order.

| Method | When it fires | `committed` |
|---|---|---|
| `OnBegin` | Immediately after the `TransactionScope` is opened | — |
| `OnCommit` | After `scope.Complete()` succeeds | — |
| `OnRollback` | When the transaction aborts (exception or `Transaction.Current.Rollback()`) | — |
| `OnComplete` | After the transaction resolves — commit **or** rollback | `true` on commit, `false` on rollback |

`OnComplete` is useful for recording execution-time metrics with a single handler regardless of outcome.

### Registering observers

```csharp
// Single observer — structured log entries at DEBUG/WARNING level
builder.Services.AddTransactionalLogging();

// Multiple observers — Composite pattern, called in registration order
builder.Services.AddTransactionalLogging()
                .AddTransactionalObserver<MetricsObserver>()
                .AddTransactionalObserver<TracingObserver>();
```

When two or more observers are registered, the proxy wraps them in a `CompositeTransactionObserver` automatically — no code changes needed in existing observers.

`AddTransactionalObserver<T>()` also registers `T` as its concrete type, so it can be injected directly alongside the interface:

```csharp
// Inject MetricsObserver directly to read counters; it is the same singleton the proxy uses.
public class MetricsController(MetricsObserver metrics) : ControllerBase { ... }
```

### Custom observer

Implement `ITransactionObserver` for any cross-cutting concern — tracing, metrics, alerting:

```csharp
public class TracingObserver : ITransactionObserver
{
    public void OnBegin(TransactionInfo info)
        => Tracer.StartSpan(info.MethodName);

    public void OnCommit(TransactionInfo info, TimeSpan elapsed) { }

    public void OnRollback(TransactionInfo info, Exception ex, TimeSpan elapsed)
        => Tracer.SetError(ex);

    public void OnComplete(TransactionInfo info, bool committed, TimeSpan elapsed)
        => Tracer.Finish(committed, elapsed);
}
```

```csharp
builder.Services.AddTransactionalObserver<TracingObserver>();
```

### Customizing the built-in logs

`AddTransactionalLogging()` is an opinionated default. To filter or silence it, use the standard .NET logging configuration — the category name is `Gsag.Transactional.Core.Observability.ITransactionObserver`:

```json
"Logging": {
  "LogLevel": {
    "Gsag.Transactional.Core.Observability.ITransactionObserver": "Warning"
  }
}
```

To change the **format**, replace it with your own observer:

```csharp
// Instead of AddTransactionalLogging()
builder.Services.AddTransactionalObserver<MyLoggingObserver>();
```

```csharp
public class MyLoggingObserver : ITransactionObserver
{
    private readonly ILogger<MyLoggingObserver> _logger;
    public MyLoggingObserver(ILogger<MyLoggingObserver> logger) => _logger = logger;

    public void OnBegin(TransactionInfo info) { }
    public void OnCommit(TransactionInfo info, TimeSpan elapsed)
        => _logger.LogInformation("[TX] {Type}.{Method} committed in {Ms}ms",
            info.DeclaringType.Name, info.MethodName, (long)elapsed.TotalMilliseconds);
    public void OnRollback(TransactionInfo info, Exception ex, TimeSpan elapsed)
        => _logger.LogError(ex, "[TX] {Type}.{Method} rolled back", info.DeclaringType.Name, info.MethodName);
    public void OnComplete(TransactionInfo info, bool committed, TimeSpan elapsed) { }
}
```

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
dotnet run --project samples/Gsag.Transactional.Demo.Api
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
| `GET /checkout/metrics` | — | Cumulative transaction counters from `InMemoryMetricsObserver` — demonstrates the Composite Observer pattern with two registered observers |
| `DELETE /checkout/reset` | — | Clears all data between demo runs |

---

## Known limitations

1. **`BeforeRollback` does not fire on `Transaction.Current.Rollback()`** — only exception-driven rollback triggers it. Programmatic rollback-vote (calling `Transaction.Current.Rollback()` then returning normally) disposes the scope without an exception, so `BeforeRollback` hooks are skipped.

2. **`DispatchProxy` does not support `out`/`ref` parameters** — attempting to proxy a method with `out` or `ref` parameters throws `NotSupportedException` at proxy-creation time.

3. **`DispatchProxy` does not support generic methods** — only generic interfaces are supported; the method itself must not have its own type parameters (e.g., `T Process<T>(...)` is not supported).

4. **Sync-path async hooks throw `NotSupportedException`** — registering an async hook (`Func<Task>`) inside a synchronous `[Transactional]` method causes `NotSupportedException` to be thrown before any hook executes. Use sync hooks (`Action`) in sync methods.

5. **Self-invocation bypasses the proxy** — calling `this.Method()` inside the same class skips `DispatchProxy`. Always inject the interface so calls route through the proxy.

6. **SQLite does not enlist in `System.Transactions`** — EF Core 9's SQLite provider sets `SupportsAmbientTransactions = false`. The proxy lifecycle works correctly; the database itself ignores the scope. For real transactional integration tests, use SQL Server, PostgreSQL, or MySQL.

7. **`CompositeTransactionObserver` is fail-fast** — if the first registered observer throws, subsequent observers are not called. This is by design but means observer registration order affects which observers receive events when one fails.

---

## Project structure

```
src/
  Gsag.Transactional.Core/     Pure library — no framework dependencies
    Attributes/                [Transactional] attribute
    Hooks/                     ITransactionHooks (BeforeCommit, BeforeRollback, AfterCommit, AfterRollback, AfterCompletion)
    Observability/             ITransactionObserver, NullTransactionObserver,
                               LoggingTransactionObserver, CompositeTransactionObserver
    Proxy/                     TransactionProxy<T>, TransactionScopeExecutor, TransactionProxyFactory
    Extensions/                AddTransactionalServices(), AddTransactionalLogging(),
                               AddTransactionalObserver<T>() DI extensions
samples/
  Gsag.Transactional.Demo.Api/ ASP.NET Core + EF Core + SQLite — e-commerce checkout demo
    Entities/                  CheckoutOrder, InventoryReservation, PaymentRecord, AuditEntry
    Data/                      CheckoutDbContext
    Exceptions/                PaymentDeclinedException, InventoryException, NotificationException
    Infrastructure/            HookOutputCollector, InMemoryEventBus (Scoped per-request),
                               InMemoryMetricsObserver (Singleton — Composite Observer demo)
    Services/                  OrderService, InventoryService, PaymentService, AuditService,
                               InventoryReportService, CheckoutService (8 scenario methods)
    Controllers/               CheckoutController (8 POST + 5 GET/DELETE)
tests/
  Gsag.Transactional.Tests/
    Unit/                      Proxy mechanics, propagation, rollback rules, observer, composite observer
    Integration/               CheckoutIntegrationTests — real SQLite, full service graph
    Integration/Hooks/         BeforeCommit, BeforeRollback, AfterCommit, AfterRollback, AfterCompletion, nested scopes
```

## Documentation

The full documentation site (guides, Mermaid diagrams, API reference) is available at
**https://gsag.github.io/Gsag.Transactional**.

To build the docs locally:

```bash
dotnet tool install -g docfx
docfx --serve    # builds and opens http://localhost:8080
```

