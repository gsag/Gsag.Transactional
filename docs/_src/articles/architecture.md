# Architecture

This page describes how `DispatchProxy`, `TransactionScope`, and `AsyncLocal` fit together inside `Gsag.Transactional.Core`.

---

## Invocation flow

```
Caller → IFoo.MethodAsync()
  └─ TransactionProxy<T>.Invoke()
       ├─ attribute cache lookup
       │    └─ [Transactional] absent → call target directly, return
       │
       └─ [Transactional] present:
            ├─ BeginScope()            (AsyncLocal hook stack)
            ├─ new TransactionScope()  (ambient transaction)
            ├─ observer.OnBegin()
            ├─ InvokeTarget()
            │
            ├─ [success]
            │    ├─ BeforeCommit hooks
            │    ├─ scope.Complete()
            │    ├─ scope.Dispose()    ← committed
            │    ├─ observer.OnCommit()
            │    ├─ AfterCommit hooks
            │    ├─ AfterCompletion hooks
            │    └─ observer.OnComplete(committed: true)
            │
            └─ [exception]
                 ├─ BeforeRollback hooks
                 ├─ scope.Dispose()    ← rolled back (no Complete)
                 ├─ observer.OnRollback()
                 ├─ AfterRollback hooks
                 ├─ AfterCompletion hooks
                 └─ observer.OnComplete(committed: false)
```

---

## TransactionProxy\<T\> — routing only

`TransactionProxy<T>` is a `DispatchProxy` subclass. Its `Invoke()` method is responsible for **routing and caching only** — all commit/rollback logic is delegated to focused infrastructure classes.

**Attribute lookup — two-step search:**  
`DispatchProxy.Invoke` always receives the **interface** `MethodInfo`. The proxy checks the interface method for `[Transactional]` first. If not found, it resolves the corresponding concrete method via `Type.GetInterfaceMap()` and checks that. This lets you place the attribute on either the interface or the concrete class.

**Cache key:** `(MethodInfo interfaceMethod, Type concreteType)`. Including the concrete type correctly handles the case where multiple implementations satisfy the same interface.

**Return-type dispatch:** After opening the scope, `Invoke()` branches by return type:
- `ValueTask` / `ValueTask<T>` — delegated to `AsyncHandler.ExecuteValueTask` / `AsyncHandler.ExecuteValueTaskGeneric`
- `Task` / `Task<T>` — delegated to `AsyncHandler.ExecuteTask`
- Synchronous — delegated to `SyncHandler.Execute`

---

## TransactionScope must be created before InvokeTarget

```csharp
// CORRECT — scope is ambient when InvokeTarget opens a DbConnection
var scope = CreateScope(attr);       // ← scope is now Transaction.Current
var task  = InvokeTarget(context);   // EF Core enlists connection here

// WRONG — scope created after InvokeTarget, connection already opened without it
var task  = InvokeTarget(context);
var scope = CreateScope(attr);       // too late
```

If the scope is created after the target begins executing, any `DbConnection` opened inside the method does not enlist in the ambient transaction, and the rollback on exception becomes a no-op at the database level.

---

## TransactionScopeAsyncFlowOption.Enabled is mandatory

By default, `TransactionScope` does not flow across `await` continuations. After an `await`, `Transaction.Current` becomes `null` in the continuation, breaking every subsequent database operation.

`TransactionScopeAsyncFlowOption.Enabled` fixes this by flowing the ambient transaction through the `ExecutionContext` — the same mechanism used by `AsyncLocal<T>`.

---

## Sync-throw-before-task

A `Task`-returning method can throw **synchronously** before it returns a `Task` (e.g., a guard clause before the first `await`). This path is handled explicitly:

1. `InvokeTarget` is wrapped in a `try/catch`
2. If it throws synchronously, the exception is captured and converted to a pre-faulted `Task` via `Task.FromException`
3. `ClearScope()` is called to restore the `AsyncLocal` state
4. The pre-faulted task is fed through the normal async rollback wrapper

This ensures `BeforeRollback`, `AfterRollback`, `AfterCompletion` hooks and all observer callbacks fire on this path — no lifecycle steps are skipped.

---

## AsyncLocal hook stack

`TransactionHooks` uses `AsyncLocal<HookCollection?>` to maintain per-scope hook registrations that flow correctly across `await` boundaries.

Each `HookCollection` carries a `Previous` pointer forming a linked-list stack. `BeginScope` reads `Transaction.Current` to decide the collection's role:

| Role | When | Behaviour |
|---|---|---|
| `Owning` | New scope (no ambient, or `RequiresNew`) | Allocated a new `HookCollection`; `ClearScope` restores `Previous` |
| `Joining` | Inner `Required` joining an outer scope | Shares the outer collection; `ClearScope` is a no-op |
| `SuppressThrowaway` | `Suppress` scope | Sets the slot to `null`; `ClearScope` restores `Previous` |

`ClearScope` is called **synchronously** from the async wrapper before returning the task to the caller, then again from `TryDispose`. The second call is guarded so it cannot clobber a new scope opened by a concurrent continuation.

---

## Infrastructure classes — focused responsibilities

The lifecycle logic is split across four focused classes, each with a single responsibility:

| Class | Responsibility |
|---|---|
| `TransactionScopeFactory` | Creates and initialises the `TransactionScope`; sets up the `AsyncLocal` hook slot via `BeginScope`; notifies the observer via `OnBegin` |
| `TransactionLifecycle` | `Commit`, `Rollback`, `TryDispose`, `NotifyCommitOutcome` — drives `scope.Complete()` / `scope.Dispose()` and all observer callbacks |
| `TransactionAsyncExecutor` | Async wrappers for `Task`, `ValueTask`, `Task<T>`, and `ValueTask<T>`; hosts the single `WrapCoreAsync<TResult>` template that owns the full async lifecycle |
| `TransactionDelegateCache` | Compiled-delegate caches for `MakeGenericMethod` calls; computed once per result type to avoid per-call reflection overhead |

`TransactionAsyncExecutor` unifies the void and result-returning async paths through a `WrapCoreAsync<TResult>` template method. A zero-byte `VoidResult` sentinel struct lets the void path share the same generic template without extra allocation.

`SyncHandler` and `AsyncHandler` are static classes that implement the sync and async execution paths respectively. Both classes open the scope via `TransactionScopeFactory`, delegate lifecycle transitions to `TransactionLifecycle`, and run hooks via `TransactionHooks`.

Rollback decisions are delegated to `RollbackPolicy` (internal). `RollbackPolicy.From(attr)` captures the attribute configuration at scope-open time; `ShouldRollback(ex)` implements a three-rule precedence check:
1. If the exception type is in `NoRollbackFor` → **commit**
2. If `RollbackFor` is non-empty and the type is not listed → **commit**
3. Otherwise → **rollback**
