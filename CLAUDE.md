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
    Proxy/TransactionScopeHelper.cs ← all commit/rollback/dispose logic and async wrappers
    Proxy/TransactionProxyFactory.cs
    Extensions/TransactionalExtensions.cs
  Transactional.Demo.Api/           ASP.NET Core + EF Core + SQLite
    Entities/Order.cs
    Data/AppDbContext.cs
    Services/{IOrderService,OrderService}.cs
    Controllers/OrdersController.cs
    Program.cs
tests/
  Transactional.Tests/
    Unit/TransactionProxyTests.cs
    Integration/OrderServiceIntegrationTests.cs
```

## Architecture

### TransactionProxy<T>

`DispatchProxy` subclass responsible for **routing and caching only** — all transactional logic lives in `TransactionScopeHelper`.

`Invoke()` flow:
1. Look up `TransactionalAttribute` from `_attributeCache` (static `ConcurrentDictionary` per-T)
2. If absent → invoke target directly via compiled delegate from `_delegateCache`
3. If present → open scope via `TransactionScopeHelper.OpenScope`, then dispatch by return type: `ValueTask`, `ValueTask<T>`, `Task`/`Task<T>`, or sync

**Critical ordering**: `TransactionScope` must be created **before** `InvokeTarget()` so it is ambient when EF Core opens its connection and enlists. Creating the scope after the task starts is a silent bug.

**`TransactionScopeAsyncFlowOption.Enabled`** is non-negotiable — without it the ambient transaction does not flow across `await` continuations.

**`HandleSync` must stay purely synchronous** — routing through `.GetAwaiter().GetResult()` corrupts `Transaction.Current` after `Dispose()`, breaking `RequiresNew` propagation (confirmed by `RequiresNew_InsideAmbientScope_SuspendsAndRestoresOuterTransaction` test).

### TransactionScopeHelper

Non-generic static class that owns all commit/rollback/dispose logic. Keeping it non-generic ensures `WrapGenericTaskMethod` and `WrapGenericValueTaskMethod` (MethodInfo fields used for `MakeGenericMethod` calls) are computed **once per application**, not once per proxied interface type.

All async wrappers use a **nested try pattern**:
- Inner try/catch handles business exceptions only
- Success-path `Commit` is outside all catch blocks — prevents double `scope.Complete()` if the observer throws during `OnCommit`
- Outer `finally` always disposes the scope

`ShouldRollback` implements the three-rule precedence: `NoRollbackFor` wins → `RollbackFor` restricts → default rolls back on any exception.

### AddTransactionalServices(Assembly)

Convention: `OrderService` → `IOrderService` (interface must be named `I{ClassName}` and live in the same assembly). Registers the concrete class as `Scoped`, then registers the interface as `Scoped` using a factory that wraps the concrete instance in a `TransactionProxy`.

### Integration tests

Each test creates a GUID-named SQLite file in `Path.GetTempPath()`. Assertions use a **fresh `DbContext` instance** (`QueryDbDirectAsync`) to avoid EF Core's change tracker returning stale in-memory entities after a DB-level rollback. `SqliteConnection.ClearAllPools()` is called in `DisposeAsync()` before deleting the file to release OS-level file locks.
