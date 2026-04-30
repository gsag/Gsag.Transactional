# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Purpose

Demo solution implementing Spring-like `@Transactional` declarative transaction management in C# using **only native .NET**. No external AOP libraries (PostSharp, AspectInjector, Castle DynamicProxy, MediatR, Autofac). Core primitives used: `DispatchProxy`, `System.Transactions.TransactionScope`, `System.Linq.Expressions`, `System.Reflection`.

## Commands

```bash
# Build
dotnet build

# Run all tests
dotnet test

# Run a single test class
dotnet test --filter "FullyQualifiedName~OrderServiceIntegrationTests"

# Run the API (Swagger at http://localhost:5000/swagger)
dotnet run --project src/Transactional.Demo.Api
```

## Solution Structure

```
src/
  Transactional.Core/               No framework dependencies
    Attributes/TransactionalAttribute.cs
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
    Integration/OrderServiceIntegrationTests.cs
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

### TransactionProxy<T>

`DispatchProxy` subclass responsible for **routing and caching only** — all transactional logic lives in `TransactionScopeExecutor`.

`Invoke()` flow:
1. Look up `TransactionalAttribute` from `_attributeCache` (static `ConcurrentDictionary<(MethodInfo, Type), TransactionalAttribute?>` per-T)
2. If absent → invoke target directly via compiled delegate from `_delegateCache`
3. If present → open scope via `TransactionScopeExecutor.OpenScope`, then dispatch by return type: `ValueTask`, `ValueTask<T>`, `Task`/`Task<T>`, or sync

**Attribute lookup — two-step search**: `DispatchProxy.Invoke` always receives the interface `MethodInfo`. The cache factory first checks the interface method for `[Transactional]`; if not found, it resolves the concrete counterpart via `GetInterfaceMap` and checks that. This means the attribute may be placed on **either** the interface or the concrete class — the proxy finds it in both cases. Cache key includes the concrete type `(MethodInfo, Type)` to support multiple implementations of the same interface.

**`[Transactional]` placement**: prefer the **concrete class**. The interface stays a clean contract. `AddTransactionalServices` discovers services by checking concrete class methods, and the proxy resolves via `GetInterfaceMap` — both sides read from the same place.

**Critical ordering**: `TransactionScope` must be created **before** `InvokeTarget()` so it is ambient when EF Core opens its connection and enlists. Creating the scope after the task starts is a silent bug.

**`TransactionScopeAsyncFlowOption.Enabled`** is non-negotiable — without it the ambient transaction does not flow across `await` continuations.

**`HandleSync` must stay purely synchronous** — routing through `.GetAwaiter().GetResult()` corrupts `Transaction.Current` after `Dispose()`, breaking `RequiresNew` propagation (confirmed by `RequiresNew_InsideAmbientScope_SuspendsAndRestoresOuterTransaction` test).

### TransactionScopeExecutor

Non-generic static class that owns all commit/rollback/dispose logic. Keeping it non-generic ensures `WrapGenericTaskMethod` and `WrapGenericValueTaskMethod` (MethodInfo fields used for `MakeGenericMethod` calls) are computed **once per application**, not once per proxied interface type.

All async wrappers use a **nested try pattern**:
- Inner try/catch handles business exceptions only
- Success-path `Commit` is outside all catch blocks — prevents double `scope.Complete()` if the observer throws during `OnCommit`
- Outer `finally` always disposes the scope

`ShouldRollback` implements the three-rule precedence: `NoRollbackFor` wins → `RollbackFor` restricts → default rolls back on any exception.

### AddTransactionalServices(Assembly)

Convention: `OrderService` → `IOrderService` (interface must be named `I{ClassName}` and live in the same assembly). Registers the concrete class as `Scoped`, then registers the interface as `Scoped` using a factory that wraps the concrete instance in a `TransactionProxy`.

### Cross-service RequiresNew pattern

`OrderFulfillmentService` demonstrates the correct way to compose two `[Transactional]` services with different propagation levels:

- **Outer** (`OrderFulfillmentService`, `Required`): receives `IOrderService` already proxied via DI
- **Inner** (`OrderService.CreateRequiresNewAsync`, `RequiresNew`): called through the inner proxy, opens its own independent scope

Self-invocation bypasses the proxy — calling `this.Method()` inside the same class skips `DispatchProxy` entirely. Always inject the dependency as its interface so the call routes through the proxy.

### Integration tests

Each test creates a GUID-named SQLite file in `Path.GetTempPath()`. WAL journal mode is enabled in `InitializeAsync` (`PRAGMA journal_mode=WAL`) so concurrent-write tests serialize cleanly without `SQLITE_BUSY` errors. Assertions use a **fresh `DbContext` instance** (`QueryDbDirectAsync`) to avoid EF Core's change tracker returning stale in-memory entities after a DB-level rollback. `SqliteConnection.ClearAllPools()` is called in `DisposeAsync()` before deleting the file to release OS-level file locks.

`BuildContext` suppresses `RelationalEventId.AmbientTransactionWarning` — EF Core SQLite does not enlist in `System.Transactions`, so this warning is expected and intentional in all tests that open `TransactionScope`s.

**SQLite limitation**: EF Core 9 SQLite provider sets `SupportsAmbientTransactions = false`. Rollback scenarios are tested by throwing **before** `SaveChangesAsync` — the scope is disposed without `Complete()`, which is the correct proxy behaviour regardless of DB enlistment. Tests involving `RequiresNew` and `NoRollbackFor` rely on this same pattern: SaveChanges commits immediately to SQLite, but the scope lifecycle (and observer events) are exercised correctly.
