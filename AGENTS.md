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
dotnet test --filter "FullyQualifiedName~OrderServiceIntegrationTests"

# Run the demo API (Swagger at http://localhost:51938/swagger)
dotnet run --project src/Transactional.Demo.Api
```

Always run `dotnet test` after any change to `Transactional.Core`. Do not skip failing tests.

## Project Structure

```
src/
  Transactional.Core/               No framework dependencies
    Attributes/TransactionalAttribute.cs
    Hooks/
      ITransactionHooks.cs          Public interface — AfterCommit, AfterRollback, AfterCompletion
      HookEvent.cs                  Enum used as dictionary key inside HookCollection
      TransactionHooks.cs           AsyncLocal-backed impl; HookCollection; BeginScope/ClearScope
    Observability/NullTransactionObserver.cs   Null Object singleton
    Proxy/TransactionProxy.cs       ← routing, caching, return-type dispatch
    Proxy/TransactionScopeExecutor.cs ← all commit/rollback/dispose logic and async wrappers
    Proxy/TransactionProxyFactory.cs
    Extensions/TransactionalExtensions.cs
  Transactional.Demo.Api/           ASP.NET Core + EF Core + SQLite
    Entities/Order.cs
    Data/AppDbContext.cs
    Services/{IOrderService,OrderService}.cs
    Services/{IOrderFulfillmentService,OrderFulfillmentService}.cs  ← cross-service RequiresNew demo
    Controllers/OrdersController.cs
    Controllers/OrderFulfillmentController.cs
    Program.cs
tests/
  Transactional.Tests/
    Unit/TransactionProxyTests.cs
    Integration/
      OrderServiceIntegrationTests.cs
      Hooks/
        AfterCommitTests.cs
        AfterRollbackTests.cs
        AfterCompletionTests.cs
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

### TransactionScopeExecutor

Non-generic static class that owns all commit/rollback/dispose logic. Keeping it non-generic ensures `WrapGenericTaskAsyncMethod` and `WrapGenericValueTaskAsyncMethod` (MethodInfo fields used for `MakeGenericMethod` calls) are computed **once per application**, not once per proxied interface type.

All async wrappers use a **nested try pattern**:
- Inner try/catch handles business exceptions only
- Success-path `Commit` is outside all catch blocks — prevents double `scope.Complete()` if the observer throws during `OnCommit`
- Outer `finally` calls `TryDispose` (captures Dispose exceptions for deferred rethrow) then `RunAsyncHooksAsync`, then rethrows any captured Dispose exception via `ExceptionDispatchInfo`

`ShouldRollback` implements the three-rule precedence: `NoRollbackFor` wins → `RollbackFor` restricts → default rolls back on any exception.

`TryDispose` vs `DisposeScope`: `TryDispose` captures the Dispose exception so hooks can still run; `DisposeScope` rethrows immediately. Use `TryDispose` in `finally` blocks, `DisposeScope` in `catch` blocks (where hooks are not expected).

`TransactionOutcome` enum drives hook dispatch: `Committed`, `CommittedWithException` (NoRollbackFor path), `RolledBack`. On rollback or `CommittedWithException` paths `suppressExceptions = true` so hook failures do not mask the original exception.

### ITransactionHooks / TransactionHooks

`ITransactionHooks` is the public interface. `TransactionHooks` (internal) is the `AsyncLocal<HookCollection?>`-backed implementation registered as a singleton.

**`HookCollection`** is the per-scope container. It holds two `Dictionary<HookEvent, List<…>>` (one for sync `Action`, one for async `Func<Task>`) allocated lazily. The `Previous` pointer forms an implicit linked-list stack so `ClearScope` can restore the slot regardless of nesting depth.

**`HookCollectionRole`** enum (`Owning`, `Joining`, `SuppressThrowaway`) drives the three paths in `ClearScope`:
- `Owning` — `ReferenceEquals` confirms ownership; restores `Previous`.
- `Joining` — no-op; outer scope owns the slot. Hooks registered inside flow into the outer collection via `_current.Value`.
- `SuppressThrowaway` — slot was set to `null` (not to this throwaway); null-check path restores `Previous`.

**`BeginScope`** is called by `OpenScope` before the `TransactionScope` is created. It reads `Transaction.Current` to detect joining and sets the `AsyncLocal` accordingly. **`ClearScope`** is called synchronously from `HandleAsync`/`HandleValueTask*` (to restore the caller's `ExecutionContext` before the returned task is awaited) and again inside `TryDispose` (the second call is harmless — guards prevent clobbering).

**Hook execution** — `RunAsyncHooksAsync` / `RunSyncHooks` dispatch by `TransactionOutcome`. On sync paths, `EnsureNoAsyncHooks` is called for all three events before any hook executes, so a `NotSupportedException` fires before any side effect.

### AddTransactionalServices(Assembly)

Convention: `OrderService` → `IOrderService` (interface must be named `I{ClassName}` and live in the same assembly). Registers the concrete class as `Scoped`, then registers the interface as `Scoped` using a factory that wraps the concrete instance in a `TransactionProxy`.

### Cross-service RequiresNew pattern

`OrderFulfillmentService` demonstrates the correct way to compose two `[Transactional]` services with different propagation levels:

- **Outer** (`OrderFulfillmentService`, `Required`): receives `IOrderService` already proxied via DI
- **Inner** (`OrderService.CreateRequiresNewAsync`, `RequiresNew`): called through the inner proxy, opens its own independent scope

Self-invocation bypasses the proxy — calling `this.Method()` inside the same class skips `DispatchProxy` entirely. Always inject the dependency as its interface so the call routes through the proxy.

## Testing Notes

### Integration tests (database)

Each test creates a GUID-named SQLite file in `Path.GetTempPath()`. WAL journal mode is enabled in `InitializeAsync` (`PRAGMA journal_mode=WAL`) to avoid `SQLITE_BUSY` errors. Assertions use a **fresh `DbContext` instance** (`QueryDbDirectAsync`) to avoid EF Core's change tracker returning stale in-memory entities after a DB-level rollback. `SqliteConnection.ClearAllPools()` is called in `DisposeAsync()` before deleting the file to release OS-level file locks.

`BuildContext` suppresses `RelationalEventId.AmbientTransactionWarning` — EF Core SQLite does not enlist in `System.Transactions`, so this warning is expected and intentional in all tests that open `TransactionScope`s.

**SQLite limitation**: EF Core 9 SQLite provider sets `SupportsAmbientTransactions = false`. Rollback scenarios are tested by throwing **before** `SaveChangesAsync` — the scope is disposed without `Complete()`, which is the correct proxy behaviour regardless of DB enlistment.

### Hook tests

`tests/…/Integration/Hooks/` tests use no database — doubles are plain C# classes with `List<string> Fired` collectors. Each test file is self-contained: service interfaces, concrete classes, and test class in the same file. `TransactionHooks` is instantiated directly (it is `internal`); proxies are created via `TransactionProxyFactory.Create`. Hook test files do not inherit from any base class or use `IAsyncLifetime`.
