# Changelog

All notable changes to this project are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

---

## [0.3.0-alpha] — 2026-05-08

### Added
- `ITransactionLifecycleObserver.OnComplete(MethodInfo, bool committed, TimeSpan)`: fires after every transaction resolves — commit or rollback — regardless of outcome. Useful for recording execution-time metrics without duplicating logic across `OnCommit` and `OnRollback`.
- `CompositeTransactionObserver`: Composite pattern over `ITransactionLifecycleObserver`. When multiple observers are registered, the proxy wraps them and calls each in registration order. Enables logging, metrics, and tracing observers to coexist without modifying any existing class.
- `AddTransactionalObserver<T>()` DI extension: registers `T` as both its concrete type (injectable directly) and as `ITransactionLifecycleObserver` (forwarded). Idempotent per type. The proxy factory builds the composite automatically when two or more observers are registered.
- `AddTransactionalLogging()` refactored to delegate to `AddTransactionalObserver<LoggingTransactionObserver>()` — combinable with additional observers.
- `InMemoryMetricsObserver` demo observer: accumulates `TotalTransactions`, `Committed`, `RolledBack`, and `TotalElapsedMs` via `Interlocked`; exposed via `GET /checkout/metrics`. Registered alongside `LoggingTransactionObserver` in the demo to exercise the Composite.
- `BeforeCommit` hook (sync + async): fires inside the `TransactionScope` before `scope.Complete()`.
  - On the success path, a throwing hook causes a rollback and `AfterRollback` fires instead of `AfterCommit`.
  - On the `NoRollbackFor` path, hook failures are suppressed so the original business exception always propagates.
- `BeforeRollback` hook (sync + async): fires inside the `TransactionScope` before `scope.Dispose()` on the rollback path.
  - Hook failures are suppressed so they cannot mask the original rollback exception.
  - Does not fire on the `NoRollbackFor` path or when rollback is voted via `Transaction.Current.Rollback()` without throwing.
- Async hook validation on sync call paths: registering an async (`Func<Task>`) hook inside a synchronous `[Transactional]` method throws `NotSupportedException` for all five hook events.

### Changed
- Solution renamed from `TransactionalDemo.sln` to `Transactional.sln`.
- Repository folders reorganized: `core/Transactional.Core`, `demo/Transactional.Demo.Api`, `tests/Transactional.Tests` (previously all under `src/`).
- `HookCollection`, `HookCollectionRole`, and `TransactionOutcome` extracted from `TransactionHooks.cs` into individual files.
- `TransactionContext` extracted from `TransactionScopeExecutor.cs` into its own file; `TransactionScopeExecutor` promoted from nested class to top-level `internal static class`.
- `DisposeScope` helper removed from `TransactionScopeExecutor` — all callers now use `TryDispose` + `NotifyCommitOutcome` directly so `OnComplete` fires on every path.

### Fixed
- `OnComplete` not fired when a method threw synchronously before returning its `Task` / `ValueTask` (the `HandleAsync`, `HandleValueTask`, and `HandleValueTaskGeneric` catch blocks in `TransactionProxy` were calling `DisposeScope`, which skipped `NotifyCommitOutcome`).
- `AvgElapsedMs` in `InMemoryMetricsObserver` was dividing by `TotalTransactions` (incremented in `OnBegin`) instead of `CompletedCount` (incremented in `OnComplete`), producing a wrong average when transactions were still in flight.

---

## [0.2.1-alpha] — 2026-05-05

### Added
- GitHub Actions CI workflow: build, test, and coverage reporting.
- NuGet and dotnet tools caching; `--locked-mode` restore with NuGet lock files.
- Test coverage for `NoRollbackFor` async variants, `TriggerSync` suppress path, and async `disposeEx` branches.

### Changed
- VoltAgent review refactoring (C1-C2, M1-M5, m1-m6, S2-S4):
  - `TransactionContext` fields made private; `TransactionScopeExecutor` nested inside `TransactionContext`.
  - `OnCommit` observer notification deferred to after `scope.Dispose()` via `NotifyCommitOutcome`.
  - Expression-tree compiled delegate caches for `Task<T>`/`ValueTask<T>` wrappers and `TransactionProxyFactory.Create(Type, …)`.
  - `HookCollection` sync/async dictionaries lazy-allocated on first use.
  - Namespace guard added to `AddTransactionalServices` interface discovery.
  - `[AttributeUsage(Inherited = false)]` added to `TransactionalAttribute`.
  - `ShouldRollback` snapshots `NoRollbackFor`/`RollbackFor` arrays before iterating.
- Project instructions migrated from `CLAUDE.md` to `AGENTS.md` (open agent format).
- GitHub Actions bumped to v5 (Node.js 24 compatible).

### Fixed
- VoltAgent M1-M3 follow-up: clarified `OnRollback` two-path design in `Commit()`; corrected double-fault test comment; removed dead `collector` variable from `CheckoutIntegrationTests.InitializeAsync`.

---

## [0.2.0] — 2026-05-05

### Added
- `ITransactionHooks` — lifecycle callback interface with `AfterCommit`, `AfterRollback`, and `AfterCompletion` hooks (sync and async overloads each).
- Hook execution semantics: sync hooks always run before async hooks; on rollback and `NoRollbackFor` paths, hook failures are suppressed so they cannot mask the original exception.
- `AsyncLocal<HookCollection?>` infrastructure with `BeginScope` / `ClearScope` and `HookCollectionRole` (`Owning`, `Joining`, `SuppressThrowaway`) for per-execution-context isolation and correct nesting behaviour.
- `TryDispose` in `TransactionScopeExecutor` — captures `Dispose` exceptions so hooks can run before the exception propagates.
- E-commerce checkout demo (`Transactional.Demo.Api`) with eight scenario endpoints covering commit, rollback, `RequiresNew`, `Suppress`, `NoRollbackFor`, and hook ordering.
- `Transactional.Core` packaged for NuGet: targets `net8.0;net9.0`, Source Link, symbol package.
- `TransactionProxy<T>` made internal — `TransactionProxyFactory` is the sole public entry point.

### Fixed
- Hooks silently dropped when `scope.Dispose()` throws `TransactionAbortedException` (e.g. `Transaction.Current.Rollback()` inside method).
- `AsyncLocal` slot leak when `observer.OnRollback` throws inside catch blocks.

---

## [0.1.0] — 2026-04-30

### Added
- `[Transactional]` attribute with `Propagation` (`Required`, `RequiresNew`, `Suppress`), `RollbackFor`, and `NoRollbackFor`.
- `TransactionProxy<T>` (`DispatchProxy`) with per-type attribute and compiled delegate caches; two-step attribute lookup (interface → concrete class via `GetInterfaceMap`).
- `TransactionScopeExecutor` — handles commit, rollback, and dispose for `Task`, `Task<T>`, `ValueTask`, `ValueTask<T>`, and sync return types; `TransactionScopeAsyncFlowOption.Enabled` ensures ambient flow across `await` continuations.
- `ITransactionLifecycleObserver` — observer callbacks on commit, rollback, and exception. `LoggingTransactionObserver` and `NullTransactionObserver` built-in implementations.
- `AddTransactionalServices(Assembly)` DI extension — convention-based proxy registration (`OrderService` → `IOrderService`).
- Cross-service `RequiresNew` composition pattern via `OrderFulfillmentService`.
- Initial demo API (`Transactional.Demo.Api`) with EF Core + SQLite.
- Unit and integration test suites.
