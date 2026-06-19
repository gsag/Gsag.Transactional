# Load Test - Gsag.Transactional

High-concurrency stress test for transaction lifecycle validation under PostgreSQL.

## Overview

Tests **9 scenarios** with **~25.3k total transactions** (optimized for comprehensive validation):

| Scenario | Operations | Details |
|----------|------------|---------|
| Pure throughput | 20,000 | 1k tasks × 20 iterations |
| Rollback vs commit | 2,000 | 50% commit / 50% rollback |
| AsyncLocal isolation | 1,000 | Hook fire validation |
| Nested RequiresNew | 300 | 150 tasks (outer + inner) |
| Nested with failure | 300 | 150 tasks (inner failure handling) |
| Exception handling | 600 | 3 exception types |
| Exception propagation | 500 | Propagation correctness |
| I/O simulation | 400 | Variable 1-10ms delays |
| Hook ordering | 400 | 400 tasks × 3 hooks each |
| **TOTAL** | **~25.3k** | **~20 seconds** |

## Prerequisites

- .NET 10.0
- Docker & Docker Compose
- PostgreSQL 16 Alpine (runs in container)

## Quick Start

```bash
cd scripts/load-test

# Automated: starts PostgreSQL, runs tests, stops PostgreSQL
./load-test.bat

# Or manual steps:
docker-compose up -d
dotnet run
docker-compose down -v
```

### Expected Runtime
- **First scenario**: 14-15 seconds
- **All 9 scenarios**: 20 seconds total
- **Teardown**: ~10 seconds

## Visual Output

Each scenario displays a **real-time progress bar** with percentage:

```
1/9  Pure throughput
[===========>          ] 52%

2/9  Rollback vs commit
[=====================> ] 95%

3/9  AsyncLocal isolation
[==========================] 100%
```

Progress updates in real-time as operations complete. No blank screen — always see activity.

## What Gets Tested

✅ Transaction lifecycle correctness under concurrency  
✅ AsyncLocal context isolation (no cross-contamination)  
✅ Hook execution ordering  
✅ Rollback vs commit semantics  
✅ Exception propagation  
✅ Nested RequiresNew transaction isolation  
✅ Memory allocation under load  
✅ GC pressure (Gen0 collections)  
✅ Transaction throughput (TPS)  

## Performance Metrics

Each scenario reports in a summary table:

```
Scenario               Transactions  Duration    TPS      Avg latency  Peak heap   Total alloc  GC0  Status
Pure throughput       100,000       20.5s       4,878    205.2 µs     128.5 MB    512.3 MB     8    ✓
Rollback vs commit     4,000         2.1s       1,904    525.0 µs     64.2 MB     256.1 MB     2    ✓
AsyncLocal isolation  2,000         1.8s       1,111    900.0 µs     32.1 MB     128.5 MB     1    ✓
...
```

- **Transactions** — Operations executed in scenario
- **Duration** — Wall-clock time for scenario
- **TPS** — Throughput (transactions/second)
- **Avg latency** — Per-transaction microseconds
- **Peak heap** — Maximum memory during test
- **Total alloc** — Bytes allocated
- **GC0** — Generation 0 garbage collections
- **Status** — ✓ (passed) or ✗ (failed)

## Database Configuration

PostgreSQL container is configured for high concurrency:
- `max_connections=500` (default 100)
- `shared_buffers=256MB` (default 128MB)

Npgsql connection pool:
- `MaxPoolSize=250` (default 100)
- `Pooling=true` (reuse connections)

## Volume Configuration

Volumes are optimized for **~20 second runtime** with full validation coverage and stable connection pooling.

To increase load for aggressive stress testing, edit `Program.cs`:

```csharp
// Multiply these for 2x heavier load (~40 seconds runtime):
const int ThroughputTasks = 2_000;          // default: 1_000
const int RollbackTasks = 4_000;            // default: 2_000
const int IsolationTasks = 2_000;           // default: 1_000
const int NestedTasks = 300;                // default: 150 (nested are sensitive to concurrency)
// ... etc
```

Or scale uniformly by changing in all scenarios:
- `1x` (current): ~25.3k ops, 20 seconds ← **recommended for CI/CD**
- `2x`: ~50.6k ops, 40 seconds ← standard stress test
- `4x`: ~101k ops, 80 seconds ← extended load testing
- Nested scenarios (4-5) scale conservatively to avoid deadlock

Progress bars work at any scale.

## Common Issues

### "too many clients already"
Means PostgreSQL connection limit hit. Verify docker-compose.yml has `max_connections=500`.

### "Connection timeout"
PostgreSQL container may not be healthy. Check:
```bash
docker-compose ps
docker logs loadtest-postgres
```

### Tests fail with "NpgsqlOperationInProgressException"
DbContext not disposed properly. Verify all `using` blocks in Services.cs.

### Progress bar stalls or is very slow
May indicate database is under heavy load. Check PostgreSQL metrics:
```bash
docker exec loadtest-postgres pg_stat_statements
```

## Architecture

```
Program.cs              — Orchestration, DI, results reporting
├── Scenarios.cs       — 9 test scenario methods
├── Services.cs        — Transactional services with @Transactional
├── Observer.cs        — ConcurrencyObserver for lifecycle validation
├── Helpers.cs         — Utilities, metrics, ScenarioResult
├── LoadTestDbContext  — EF Core schema (Entity: Id, Value)
├── SystemInfo.cs      — Machine metrics collection
└── LifecycleAccumulator — Validation state aggregation
```

## Data Model

Single minimalist entity:
```csharp
class Entity
{
    public int Id { get; set; }
    public int Value { get; set; }
}
```

Focused on testing **concurrency/isolation**, not domain logic.

## Validation

**Lifecycle Consistency:** Each transaction must follow Begin → (Commit|Rollback) → Complete.
- Orphaned transactions (missing Complete)
- Incomplete transitions
- Invalid state sequences

Validation runs across all 9 scenarios and reports errors with scenario numbers.

## Future Enhancements

- Parameterize task counts for variable load levels
- Add latency distribution histograms
- Export metrics to Prometheus/Grafana
- Multi-database backend support (SQL Server, MySQL)

## References

- `.NET Transactions`: System.Transactions.TransactionScope
- **Gsag.Transactional.Core**: @Transactional attribute, DispatchProxy interception
- **Npgsql**: PostgreSQL driver with connection pooling
- **Entity Framework Core**: DbContextFactory pattern for thread-safety
