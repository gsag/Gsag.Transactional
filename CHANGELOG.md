# Changelog

All notable changes to this project are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

---

## [0.5.4] — 2026-07-15

### Fixed
- **Observability article missing from docs**: Added `observability.md` to the DocFX table of contents so the article appears in the documentation sidebar and GitHub Pages site.

---

## [0.5.3] — 2026-07-15

### Added
- **OpenTelemetry observability sample** (`Gsag.Transactional.Observability`): Full observability pipeline demonstrating how to integrate OpenTelemetry with the transactional observer. Includes:
  - `OpenTelemetryTransactionObserver` — records transaction counters, duration histograms, and activities via OTLP.
  - `ObservabilityOptions` — configuration model for tracing, metrics, and logs (Grpc/HttpProtobuf protocols).
  - `AddObservabilityPipeline(IConfiguration)` — single-call registration for OpenTelemetry pipeline, Serilog log export, health checks, and landing page.
  - `AddObservability()` — builder extension to register the observer via `AddTransactional`.
  - Health checks for PostgreSQL and Grafana (`/health/ready`, `/health/live`).
  - Landing page dashboard at `/` with HTMX-powered live health badges and Lucide icons.
  - `ObservabilityStartupFilter` — `IStartupFilter` auto-mapping endpoints on startup.
  - Configuration-driven setup via `appsettings.json` (`Observability` section).
  - Grafana LGTM stack (`grafana/otel-lgtm`) replacing Jaeger + Prometheus + OTel Collector.
  - PostgreSQL 18 upgrade in docker-compose.
- **Invocation strategy pattern** for return-type routing in `TransactionProxy`: new `Proxy/Invocation/` types (`ITransactionInvocationStrategy`, `TransactionInvocationStrategyResolver`, `SyncInvocationStrategy`, `TaskInvocationStrategy`, `ValueTaskInvocationStrategy`, `ValueTaskGenericInvocationStrategy`, `UnsupportedAsyncLikeInvocationStrategy`) replace inline routing logic.
- **Unsupported async-like return type detection**: `TransactionProxy` now detects `IAsyncEnumerable<T>` and custom awaitable types, bypasses transaction wrapping, and emits a `Trace.TraceWarning`. Guidance added to architecture and limitations docs.
- **Demo API documentation**: Simplified README with project structure, endpoint reference, and extension guide.
- **Load-test enhancements** (`scripts/load-test/`): OpenTelemetry OTLP exporter integration, Serilog structured logging, concurrency and throughput validation improvements.

### Changed
- **`TransactionProxy` refactored** to use strategy pattern for return-type routing, improving testability and separation of concerns.
- **SonarCloud CI fixes**: Resolved S4036 (absolute git path), S2325 (static methods), S2701 (assertions), NU1510 (unnecessary packages).
- **Test assertions**: Replaced `Assert.Equal(true/false)` with `Assert.True()/Assert.False()`.

### Fixed
- **Async-like return types** (`IAsyncEnumerable<T>`, custom awaitables) no longer cause proxy failures; they are detected and bypassed with a trace warning.
- **Extension-style `GetAwaiter` methods**: `TransactionProxy` now recognizes awaitable types provided via extension methods.
- **ValueTask boxed CA2012 warning**: Suppressed in `ValueTaskInvocationStrategy` (boxed value returned through `DispatchProxy.Invoke`).

### Dependencies
- `Testcontainers.PostgreSql` bumped from 4.12.0 to 4.13.0.
- `dotnet-stryker` bumped from 4.14.2 to 4.16.0.
- `Npgsql.EntityFrameworkCore.PostgreSQL` bumped from 10.0.2 to 10.0.3.
- `Microsoft.NET.Test.Sdk` bumped from 18.6.0 to 18.7.0.
- `Microsoft.EntityFrameworkCore.Design` bumped from 10.0.2 to 10.0.9.
- `Swashbuckle.AspNetCore` bumped from 10.1.7 to 10.2.3.
- `Testcontainers.PostgreSql` bumped from 3.9.0 to 4.12.0.
- `actions/cache` bumped from v5 to v6.
- `actions/checkout` bumped from v6 to v7.

---

## [0.5.2] — 2026-06-12

### Added
- **Auto-discovery of calling assembly**: `AddTransactional()` now automatically scans the assembly from which it is called, eliminating the need for explicit `.ScanAssembly()` in most applications. Services with `[Transactional]` methods and matching `I{ClassName}` interfaces are discovered without configuration. Simplifies typical usage: `builder.Services.AddTransactional();` instead of `builder.Services.AddTransactional(b => b.ScanAssembly(typeof(MyService).Assembly));`.
- **Explicit ScanAssembly override behavior**: Calling `.ScanAssembly()` explicitly now **overwrites** auto-discovery, allowing developers to scan different or additional assemblies when needed. Multiple `.ScanAssembly()` calls scan all specified assemblies. Behavior is clearly documented to prevent confusion.
- **Comprehensive test coverage for discovery**: 12 new tests validate:
  - Auto-discovery functionality and proxy generation
  - Service isolation (only matching services are registered)
  - Exclusion of orphan services (no matching interface)
  - Exclusion of services in different namespaces
  - ScanAssembly override behavior
  - Multiple assembly scanning
  - AddService suppression of auto-discovery

### Changed
- **Documentation clarity**: Updated README, getting-started, and installation articles with explicit examples showing:
  - Default auto-discovery behavior
  - ScanAssembly as optional override with `IMPORTANT` note
  - Multiple assembly scanning patterns
- **XML documentation**: `ITransactionalBuilder.ScanAssembly()` updated with `IMPORTANT` notice that calling the method overwrites automatic calling-assembly discovery.

### Fixed
- **Documentation rebuild**: Regenerated static site with DocFX to reflect all auto-discovery changes in API reference.

### Test Coverage
- Total tests: 256 (was 245)
- New tests: 12
  - 6 auto-discovery behavior tests
  - 6 service isolation tests
- All tests passing ✓

---

## [0.5.1] — 2026-06-10

### Added
- **Fluent builder pattern for transactional configuration**: New `ITransactionalBuilder` interface and `AddTransactional()` entry point consolidate four extension methods into a chainable API. Improves discoverability and groups configuration concerns. See [Installation](docs/_src/articles/installation.md) for examples.
- **Load/stress testing framework**: `load-test.bat` and translated `load-test.ps1` for concurrent throughput validation; measures latency, allocation, GC pressure, and heap sampling under high load (1000+ concurrent tasks).
- **Package validation baseline suppressions**: Explicit `PackageValidationBaselineSuppressions.xml` documents intentional breaking changes during 0.x.x pre-release development; allows API consolidation without quality-gate failure.
- **Enhanced test coverage**: New tests for `SyncHandler` dispose path edge cases and `NoRollbackFor` behavior under concurrent execution.

### Changed
- **API Consolidation** (breaking for 0.x.x): Consolidated `AddTransactionalServices()`, `AddTransactionalLogging()`, `AddTransactionalObserver<T>()`, and `AddTransactionalService<TI, TImpl>()` into fluent builder `AddTransactional()`. All functionality preserved; see [Unreleased migration guide](docs/_src/articles/installation.md). Suppressed via package validation baseline.
- **Proxy performance**: Use `Array.Empty` for null arguments instead of allocating new arrays in `TransactionProxy.Invoke()`.
- **Cache RollbackPolicy**: Extracted `RollbackPolicy` from `TransactionScopeExecutor` and cached per method to avoid re-parsing attributes on every invocation.
- **Renamed async executor**: `TransactionAsyncRunner` → `TransactionAsyncExecutor` for clarity and consistency with sync counterpart `SyncHandler`.
- **SRP refactoring**: Split `TransactionScopeExecutor` into focused classes (`SyncHandler`, `AsyncHandler`) with clear responsibilities; extracted `TransactionLifecycle` for commit/rollback orchestration.
- **Test project reorganization**: Restructured into `Core/` (unit, integration) and `Demo/` (checkout scenario) top-level folders for improved navigation and maintenance.
- **Build documentation**: Rebuild static site after architecture and proxy refactoring; updated architecture.md and TransactionAsyncExecutor references.
- **Load-test localization**: Translated `load-test.ps1` script to English; moved to dedicated `scripts/load-test/` folder with Windows batch shortcut for accessibility.

### Fixed
- **Build-docs execution**: Fixed DocFX initialization by running from repo root instead of subdirectory; ensures `docfx.json` discovery.
- **Documentation consistency**: Updated all examples (README, articles, CHANGELOG) to reflect new `AddTransactional()` builder pattern.

### Performance
- Array allocation overhead eliminated in proxy hot path via `Array.Empty` reuse.
- Per-method `RollbackPolicy` caching reduces reflection overhead on repeated invocations (significant under high concurrency).
- Load-test metrics added: GC count, allocation size, peak heap; validates throughput, latency, and memory efficiency under 1000+ concurrent tasks.

---

## [0.5.0] — 2026-05-22

### Added
- `.NET 10 support`: Core library now targets `.NET 8.0;net9.0;net10.0`; tests and demo upgraded to `.NET 10`; publish workflow validates package across all three versions.
- `[SuppressMessage]` attributes across `TransactionProxy`, `TransactionScopeExecutor`, and `TransactionalExtensions` to document and suppress known static-analysis false positives for intended behaviors (per-T cache isolation, outcome tracking flow).
- SonarCloud quality gate integration: standardized to ubuntu-latest runners, consolidated JSON report output.

### Changed
- **CI/Workflow Modernization**: Replaced Codecov with SonarCloud (org: `gsag`, project: `Gsag.Transactional`); coverage format changed from Cobertura to OpenCover.
- CI workflows unified into two parallel jobs (`Build·Test·Analysis` and `Quality Gate`) on ubuntu-latest, eliminating cross-job artifact passing and reducing redundancy.
- Nightly workflow refactored into three parallel jobs (`build-test-analysis`, `quality-gate`, `mutation`) with consolidated report output; all jobs now check out from `main` with full history for SonarCloud analysis.
- `TransactionProxy.HandleAsync()` return type: `object` → `Task` (improves type safety and compiler optimizations on hot path).
- `TransactionProxy.HandleValueTask()` return type: `object` → `ValueTask` (same rationale: type safety and async performance).
- Test assertions: replaced `Assert.IsAssignableFrom<T>()` with `Assert.IsType<T>(obj, exactMatch: false)` for clarity (7 occurrences).
- Test factory calls: converted non-generic `TransactionProxyFactory.Create(Type, object, observer)` to generic `Create<T>(instance, observer)` overload for type safety (5 occurrences in ProxyFactoryTests).

### Fixed
- Security hardening: moved secret expansion in `publish.yml` from inline run block to environment variable (via `env:` block) to prevent accidental exposure in logs.
- SonarCloud analysis: made Sonar steps conditional on `SONAR_TOKEN` availability to prevent spurious failures when token is unavailable.
- Test methods marked as `static` when they don't access instance data (`ExtensionsTests.Run`, `ProxyMechanicsTests.Method`).
- Logging observer generic type: `LoggingTransactionObserver` now uses `ILogger<LoggingTransactionObserver>` instead of untyped logger, ensuring logs are categorized correctly in structured logging systems.
- Documentation: revised `AGENTS.md` project guidelines; updated README for SonarCloud and removed obsolete Codecov references.
- Build scripts: refactored `docs/_src/build.ps1` for improved UI/UX flow; organized top-level build scripts.

### Dependencies
- `dotnet-sonarscanner` added (11.2.1) to `.config/dotnet-tools.json` for CI SonarCloud integration.
- `dotnet-stryker` bumped from 4.14.1 to 4.14.2.
- `coverlet.collector` bumped from 10.0.0 to 10.0.1.
- `xunit.runner.visualstudio` bumped from 3.0.0 to 3.1.5.

---

## [0.5.0-alpha] — 2026-05-15

### Added
- `AddTransactionalService<T, I>()` explicit overload: registers a concrete type `T` paired with interface `I` without relying on the `I{ClassName}` naming convention.
- `[RequiresUnreferencedCode]` on `AddTransactionalServices` for trim analysis safety; suppresses `IL2072` inside `TransactionProxy<T>` with `[UnconditionalSuppressMessage]` and a justification comment.
- Stryker mutation testing integrated into the nightly CI pipeline; `stryker-config.json` with `test-case-filter` to exclude Demo integration tests from mutation runs.
- Nightly CI workflow (`nightly.yml`): `build → test → quality-gate + mutation (parallel) → consolidated report`; triggered by schedule (23:00 UTC) and `workflow_dispatch`.
- Quality gate expanded with trim compatibility analysis (`EnableTrimAnalyzer`) and API compatibility check (`EnablePackageValidation` against the latest published NuGet release).
- `.config/dotnet-tools.json` tool manifest pinning `reportgenerator 5.5.10` and `dotnet-stryker 4.14.1`; all workflows use `dotnet tool restore` instead of ad-hoc global installs.
- `codecov.yml` enforcing ≥ 90% coverage on both project and patch for every PR.

### Changed
- `RollbackPolicy` extracted from `TransactionScopeExecutor`: rollback decisions delegated to `RollbackPolicy.From(attr)` / `ShouldRollback(ex)`, applying the three-rule precedence (`NoRollbackFor` → `RollbackFor` → default rollback).
- `TransactionScopeExecutor` async wrappers (`Task`, `Task<T>`, `ValueTask`, `ValueTask<T>`) refactored via Template Method pattern, eliminating duplication across all four paths.
- `ValueTask` synchronous fast path restored in async cores.
- `FindAttribute` for-loop replaced with `Array.FindIndex` + ternary, removing the compiler-required unreachable trailing `return null`.
- CI restructured into granular reusable workflows: `_build.yml`, `_test.yml`, `_quality-gate.yml`, `_mutation.yml`.
- Build and test split into separate pipeline stages; build output transferred between jobs as a tar archive to preserve project-relative paths.
- All workflow runners standardised to `windows-latest`.
- Coverage threshold raised to 90% (line and branch).
- .NET SDK setup simplified from three versions (8/9/10) to a single `10.0.x`.
- CI triggers extended: `ci.yml` now also runs on PRs targeting `develop`.

### Fixed
- Demo: Swagger browser now opens automatically on startup.
- Timeout tests: resolved intermittent flakiness.
- `FindAttribute`: added test using `DynamicMethod` (null `DeclaringType`) to cover the defensive guard preventing `GetInterfaceMap` from being called with a null argument.

### Dependencies
- `Microsoft.EntityFrameworkCore.Sqlite` and `Microsoft.EntityFrameworkCore.Design` bumped to 9.0.16.
- `coverlet.collector` bumped to 10.0.0.
- `Microsoft.NET.Test.Sdk` bumped to 18.5.1.
- `Microsoft.Extensions.DependencyInjection.Abstractions` and `Microsoft.Extensions.Logging.Abstractions` bumped to latest.
- `codecov/codecov-action` bumped from v5 to v6.

---

## [0.4.1-alpha] — 2026-05-12

### Added
- DocFX documentation site targeting GitHub Pages: 7 articles (installation, getting started, propagation modes, transaction hooks, rollback rules, limitations, architecture), API reference generated from XML comments, dark mode, search.
- `docs/_src/build.ps1` — local preview script (clean → metadata → serve at `http://localhost:8080`).
- NuGet publish workflow (`.github/workflows/publish.yml`): packs and pushes `Gsag.Transactional.Core` to NuGet.org on `v*` tag push.
- Codecov integration: coverage uploaded from CI and badge added to README.
- Dependabot configuration for NuGet and GitHub Actions dependency updates.
- `.editorconfig` and `Directory.Build.props` for consistent code style across the solution.
- MinVer for automatic version resolution from git tags.
- Test suite expanded: `TimeoutTests`, `CancellationTests`, `StressTests`, `NestedPropagationTests` (144 tests total).

### Changed
- Repository restructured to OSS layout: `src/`, `samples/`, `tests/`.
- Public API stabilized: implementation types internalized, namespaces aligned, `ITransactionLifecycleObserver` renamed to `ITransactionObserver`.
- `TransactionalAttribute.TimeoutSeconds`: changed from `int?` to `int` (`0` = system default) to satisfy the C# named attribute argument constraint (CS0655).
- Unit tests reorganized into subfolders mirroring the source structure.

### Fixed
- `ITransactionObserver.OnRollback` XML comment: clarified it is not called when a `BeforeRollback` hook exception is suppressed.

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
- `TransactionProxy<T>` and `TransactionProxyFactory` are internal — proxy creation is exposed only via the DI extensions.

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
