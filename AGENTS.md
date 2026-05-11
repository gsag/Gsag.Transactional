# AGENTS.md

Project instructions for AI coding agents. Complements README with the technical context needed to work safely in this codebase.

## Overview

Demo library implementing Spring-like `@Transactional` declarative transaction management in C# using **only native .NET** — no external AOP libraries (PostSharp, AspectInjector, Castle DynamicProxy, MediatR, Autofac). Core primitives: `DispatchProxy`, `System.Transactions.TransactionScope`, `System.Linq.Expressions`, `System.Reflection`.

## Commands

```bash
# Build
dotnet build

# Run all tests
dotnet test

# Run a single test class
dotnet test --filter "FullyQualifiedName~CheckoutIntegrationTests"

# Run the demo API (Swagger at http://localhost:51938/swagger)
dotnet run --project samples/Gsag.Transactional.Demo.Api
```

Always run `dotnet test` after any change to `Gsag.Transactional.Core`. Do not skip failing tests.

## Project Structure

```
src/
  Gsag.Transactional.Core/          No framework dependencies
    Attributes/
      TransactionalAttribute.cs
    Hooks/
      ITransactionHooks.cs          Public interface — BeforeCommit, BeforeRollback, AfterCommit, AfterRollback, AfterCompletion
      HookEvent.cs                  Enum used as dictionary key inside HookCollection
      HookCollection.cs             Per-scope container (sync + async dictionaries, Previous pointer)
      HookCollectionRole.cs         Owning / Joining / SuppressThrowaway
      TransactionOutcome.cs         Committed / CommittedWithException / RolledBack
      TransactionHooks.cs           AsyncLocal-backed impl; BeginScope/ClearScope
    Observability/
      ITransactionLifecycleObserver.cs  OnBegin / OnCommit / OnRollback / OnComplete
      NullTransactionObserver.cs        Null Object singleton
      LoggingTransactionObserver.cs     MEL-based built-in observer (Debug + Warning)
      CompositeTransactionObserver.cs   Composite — dispatches to N observers in order (fail-fast)
    Proxy/
      TransactionContext.cs         Per-invocation context (method, scope, attr, stopwatch, observer, hooks)
      TransactionProxy.cs           ← routing, caching, return-type dispatch
      TransactionScopeExecutor.cs   ← all commit/rollback/dispose logic and async wrappers
      TransactionProxyFactory.cs
    Extensions/
      TransactionalExtensions.cs    AddTransactionalServices / AddTransactionalLogging / AddTransactionalObserver<T>
samples/
  Gsag.Transactional.Demo.Api/      ASP.NET Core + EF Core + SQLite
    Entities/
      CheckoutOrder.cs
      InventoryReservation.cs
      PaymentRecord.cs
      AuditEntry.cs
    Data/
      CheckoutDbContext.cs
    Exceptions/
      PaymentDeclinedException.cs
      InventoryException.cs
      NotificationException.cs
    Infrastructure/
      HookOutputCollector.cs        Scoped per-request hook log collector
      IEventBus.cs / InMemoryEventBus.cs  Scoped in-memory event bus
      InMemoryMetricsObserver.cs    Singleton composite-observer demo — Interlocked counters
    Services/
      IOrderService / OrderService
      IInventoryService / InventoryService
      IPaymentService / PaymentService
      IAuditService / AuditService        ← RequiresNew inner scope demo
      ICheckoutService / CheckoutService  ← outer Required scope; orchestrates all inner services
      IInventoryReportService / InventoryReportService  ← Suppress demo
    Controllers/
      CheckoutController.cs         8 POST scenarios + 5 GET/DELETE utility endpoints
    appsettings.json
    appsettings.Development.json    Transactional.Core → Debug (enables observer logs locally)
    Program.cs
tests/
  Gsag.Transactional.Tests/
    Unit/
      TestHelpers.cs                RecordingObserver + shared doubles
      CompositeObserverTests.cs     OnComplete, Composite multi-observer, fail-fast
      ExecutorEdgeCaseTests.cs
      ExtensionsTests.cs            DI registration, AddTransactionalObserver idempotency
      LoggingObserverTests.cs
      ObserverTests.cs              OnBegin/OnCommit/OnRollback/OnComplete per return type
      PropagationTests.cs
      ProxyFactoryTests.cs
      ProxyMechanicsTests.cs
      RollbackRulesTests.cs
    Integration/
      Demo/
        CheckoutIntegrationTests.cs ← real SQLite, full service graph
      Hooks/
        AfterCommitTests.cs
        AfterRollbackTests.cs
        AfterCompletionTests.cs
        BeforeCommitTests.cs
        BeforeRollbackTests.cs
        HookScopeTests.cs
        HookErrorTests.cs
        SyncPathHookTests.cs
```

## Code Style

Always use braces for `if`, `for`, `foreach`, and `while` bodies, even when the body is a single line.

```csharp
// correct
if (interfaceType is null)
{
    continue;
}

// wrong
if (interfaceType is null)
    continue;
```

## Architecture

### TransactionProxy\<T\>

`DispatchProxy` subclass responsible for **routing and caching only** — all transactional logic lives in `TransactionScopeExecutor`.

`Invoke()` flow:
1. Look up `TransactionalAttribute` from `_attributeCache` (static `ConcurrentDictionary<(MethodInfo, Type), TransactionalAttribute?>` per-T)
2. If absent → invoke target directly via compiled delegate from `_delegateCache`
3. If present → open scope via `TransactionScopeExecutor.OpenScope`, then dispatch by return type: `ValueTask`, `ValueTask<T>`, `Task`/`Task<T>`, or sync

**Attribute lookup — two-step search**: `DispatchProxy.Invoke` always receives the interface `MethodInfo`. The cache factory first checks the interface method for `[Transactional]`; if not found, it resolves the concrete counterpart via `GetInterfaceMap` and checks that. The attribute may be placed on **either** the interface or the concrete class. Cache key includes the concrete type `(MethodInfo, Type)` to support multiple implementations of the same interface.

**`[Transactional]` placement**: prefer the **concrete class**. The interface stays a clean contract. `AddTransactionalServices` discovers services by checking concrete class methods, and the proxy resolves via `GetInterfaceMap` — both sides read from the same place.

**Critical ordering**: `TransactionScope` must be created **before** `InvokeTarget()` so it is ambient when EF Core opens its connection and enlists. Creating the scope after the task starts is a silent bug.

**`TransactionScopeAsyncFlowOption.Enabled`** is non-negotiable — without it the ambient transaction does not flow across `await` continuations.

**`HandleSync` must stay purely synchronous** — routing through `.GetAwaiter().GetResult()` corrupts `Transaction.Current` after `Dispose()`, breaking `RequiresNew` propagation (confirmed by `RequiresNew_InsideAmbientScope_SuspendsAndRestoresOuterTransaction` test).

**Sync-throw-before-task**: if `InvokeTarget` throws synchronously before returning its `Task`/`ValueTask`, the exception is caught and converted to a pre-faulted task (`Task.FromException`, `ValueTask.FromException`, or the generic variants via `CreateFaultedTask`/`CreateFaultedValueTask` compiled delegates). `ClearScope` is then called unconditionally and the faulted task is fed into the normal async wrapper — ensuring the full rollback lifecycle (BeforeRollback hooks, observer notifications, AfterRollback/AfterCompletion hooks) runs on this path without any duplication.

### TransactionScopeExecutor

Non-generic static class that owns all commit/rollback/dispose logic. Keeping it non-generic ensures `WrapGenericTaskAsyncMethod` and `WrapGenericValueTaskAsyncMethod` (MethodInfo fields used for `MakeGenericMethod` calls) are computed **once per application**, not once per proxied interface type.

All async wrappers use a **nested try pattern**:
- Inner try/catch handles business exceptions only
- Success-path `Commit` is outside all catch blocks — prevents double `scope.Complete()` if the observer throws during `OnCommit`
- Outer `finally` calls `TryDispose` (captures Dispose exceptions for deferred rethrow) then `NotifyCommitOutcome` then `RunAsyncHooksAsync`, then rethrows any captured Dispose exception via `ExceptionDispatchInfo`

`ShouldRollback` implements the three-rule precedence: `NoRollbackFor` wins → `RollbackFor` restricts → default rolls back on any exception.

`TryDispose`: captures the Dispose exception and returns it, so `NotifyCommitOutcome` and hooks can still run before the exception is rethrown via `ExceptionDispatchInfo`. Used in every `finally` block and in the sync-throw-before-task `catch` blocks in `TransactionProxy`.

`NotifyCommitOutcome`: called after `TryDispose` to fire `OnCommit`/`OnRollback` and always `OnComplete` on the observer. On the `RolledBack` path it calls only `OnComplete(committed: false)` — `OnRollback` was already called by `Rollback()`. On commit paths it calls `OnCommit` then `OnComplete(committed: true)`; if Dispose threw, it calls `OnRollback(disposeEx)` then `OnComplete(committed: false)` instead.

`TransactionOutcome` enum drives hook dispatch: `Committed`, `CommittedWithException` (NoRollbackFor path), `RolledBack`. On rollback or `CommittedWithException` paths `suppressExceptions = true` so hook failures do not mask the original exception.

### ITransactionLifecycleObserver / CompositeTransactionObserver

`ITransactionLifecycleObserver` has four methods: `OnBegin`, `OnCommit`, `OnRollback`, `OnComplete`. `OnComplete` fires after every transaction resolves — commit or rollback — and carries a `committed` bool and elapsed `TimeSpan`.

`CompositeTransactionObserver` (internal) wraps a list of observers and dispatches to each in registration order. Exceptions propagate immediately (fail-fast — second observer is not called if the first throws).

`AddTransactionalObserver<T>()` registers `T` as a Singleton both as `T` and as `ITransactionLifecycleObserver` (via forwarding factory). An `ObserverRegistered<T>` marker type prevents duplicate registration. The proxy factory resolves all registered `ITransactionLifecycleObserver` instances via `GetServices<T>()` and builds a composite when there are two or more.

`AddTransactionalLogging()` delegates to `AddTransactionalObserver<LoggingTransactionObserver>()` — it is fully composable with other observers.

### ITransactionHooks / TransactionHooks

`ITransactionHooks` is the public interface. `TransactionHooks` (internal) is the `AsyncLocal<HookCollection?>`-backed implementation registered as a singleton.

**`HookCollection`** is the per-scope container. It holds two `Dictionary<HookEvent, List<…>>` (one for sync `Action`, one for async `Func<Task>`) allocated lazily. The `Previous` pointer forms an implicit linked-list stack so `ClearScope` can restore the slot regardless of nesting depth.

**`HookCollectionRole`** enum (`Owning`, `Joining`, `SuppressThrowaway`) drives the three paths in `ClearScope`:
- `Owning` — `ReferenceEquals` confirms ownership; restores `Previous`.
- `Joining` — no-op; outer scope owns the slot. Hooks registered inside flow into the outer collection via `_current.Value`.
- `SuppressThrowaway` — slot was set to `null` (not to this throwaway); null-check path restores `Previous`.

**`BeginScope`** is called by `OpenScope` before the `TransactionScope` is created. It reads `Transaction.Current` to detect joining and sets the `AsyncLocal` accordingly. **`ClearScope`** is called synchronously from `HandleAsync`/`HandleValueTask*` (to restore the caller's `ExecutionContext` before the returned task is awaited) and again inside `TryDispose` (the second call is harmless — guards prevent clobbering).

**Hook execution** — `RunAsyncHooksAsync` / `RunSyncHooks` dispatch by `TransactionOutcome`. On sync paths, `EnsureNoAsyncHooks` is called for all five hook events before any hook executes, so a `NotSupportedException` fires before any side effect.

### AddTransactionalServices(Assembly)

Convention: `OrderService` → `IOrderService` (interface must be named `I{ClassName}` and live in the same assembly and namespace). Registers the concrete class as `Scoped`, then registers the interface as `Scoped` using a factory that wraps the concrete instance in a `TransactionProxy`. `ITransactionHooks` is registered as a Singleton (idempotent via `TryAddSingleton`).

### Cross-service composition patterns

**RequiresNew** — `CheckoutService` (outer `Required`) calls `AuditService.WriteAsync` which uses `[Transactional(RequiresNew)]`. The audit scope opens independently, commits, and returns before the outer scope resolves. If the outer scope later rolls back, the audit entry is already durable.

**Suppress** — `CheckoutService` (outer `Required`) calls `InventoryReportService.ReadAvailableStockAsync` which uses `[Transactional(Suppress)]`. The ambient transaction is suspended for the duration of the call; `Transaction.Current` is `null` inside. The outer scope is automatically resumed when the call returns.

Self-invocation bypasses the proxy — calling `this.Method()` inside the same class skips `DispatchProxy` entirely. Always inject the dependency as its interface so the call routes through the proxy.

## Testing Notes

### Integration tests (database)

Each test creates a GUID-named SQLite file in `Path.GetTempPath()`. WAL journal mode is enabled in `InitializeAsync` (`PRAGMA journal_mode=WAL`) to avoid `SQLITE_BUSY` errors. Assertions use a **fresh `DbContext` instance** (`QueryDbDirectAsync`) to avoid EF Core's change tracker returning stale in-memory entities after a DB-level rollback. `SqliteConnection.ClearAllPools()` is called in `DisposeAsync()` before deleting the file to release OS-level file locks.

`BuildContext` suppresses `RelationalEventId.AmbientTransactionWarning` — EF Core SQLite does not enlist in `System.Transactions`, so this warning is expected and intentional in all tests that open `TransactionScope`s.

**SQLite limitation**: EF Core 9 SQLite provider sets `SupportsAmbientTransactions = false`. Rollback scenarios are tested by throwing **before** `SaveChangesAsync` — the scope is disposed without `Complete()`, which is the correct proxy behaviour regardless of DB enlistment.

### Hook tests

`tests/…/Integration/Hooks/` tests use no database — doubles are plain C# classes with `List<string> Fired` collectors. Each test file is self-contained: service interfaces, concrete classes, and test class in the same file. `TransactionHooks` is instantiated directly (it is `internal`); proxies are created via `TransactionProxyFactory.Create`. Hook test files do not inherit from any base class or use `IAsyncLifetime`.
