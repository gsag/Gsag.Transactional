# Load Test - Gsag.Transactional

High-concurrency stress test for transaction lifecycle validation under PostgreSQL.

## Overview

Tests 9 scenarios with up to **1M concurrent transactions**:
- Pure throughput (20k tasks × 50 iterations)
- Rollback vs commit (40k tasks, 50% failure)
- AsyncLocal isolation (20k tasks, hook fire validation)
- Nested RequiresNew transactions (10k tasks)
- Nested with inner failure (8k tasks)
- Exception handling (15k tasks, 3 exception types)
- Exception propagation correctness (10k tasks)
- I/O simulation (5k tasks with variable delays)
- Hook ordering validation (6k tasks, 3 hooks each)

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

Each scenario reports:
- **Transactions** — Operations executed
- **Duration** — Total wall-clock time
- **TPS** — Throughput (transactions/second)
- **Avg latency** — Per-transaction microseconds
- **Peak heap** — Maximum memory during test
- **Total alloc** — Bytes allocated
- **GC0** — Generation 0 collections
- **Status** — ✓ (passed) or ✗ (failed)

## Database Configuration

PostgreSQL container is configured for high concurrency:
- `max_connections=500` (default 100)
- `shared_buffers=256MB` (default 128MB)

Npgsql connection pool:
- `MaxPoolSize=250` (default 100)
- `Pooling=true` (reuse connections)

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
