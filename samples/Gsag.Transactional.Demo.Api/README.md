# Demo API — E-Commerce Checkout

Example ASP.NET Core API demonstrating the `[Transactional]` attribute library through a realistic checkout workflow.

## Running

**Requirements:** Docker (PostgreSQL container is managed automatically)

```bash
dotnet run
```

Swagger UI opens automatically at `http://localhost:51938/swagger` (HTTPS: `https://localhost:51937/swagger`).

## What It Demonstrates

Eight isolated endpoints, each showcasing a distinct transactional behavior:

| Endpoint | Behavior | What to look for |
|---|---|---|
| `POST /checkout/success` | `Required` propagation | All services join outer scope; AuditService commits independently via `RequiresNew` |
| `POST /checkout/payment-failure` | Rollback on exception | PaymentService throws; outer scope disposed without `Complete()` |
| `POST /checkout/inventory-failure` | Rollback pattern | InventoryService throws; no data persists |
| `POST /checkout/audit-requires-new` | `RequiresNew` scope | Audit entry commits independently; outer scope rolls back but audit survives |
| `POST /checkout/no-rollback-for` | `NoRollbackFor` config | NotificationException commits despite being thrown |
| `POST /checkout/after-commit-hook` | Lifecycle hooks | AfterCommit hook fires only after outer scope commits |
| `POST /checkout/after-rollback-hook` | Compensating hooks | Multiple AfterRollback hooks execute in order after rollback |
| `POST /checkout/suppress` | `Suppress` propagation | InventoryReportService runs outside ambient transaction |
| `GET /checkout/orders` | Query | List all persisted orders |
| `GET /checkout/audit-log` | Query | List all audit entries |
| `GET /checkout/payments` | Query | List all payment records |
| `GET /checkout/metrics` | Observability | Transaction metrics from the Composite Observer |
| `DELETE /checkout/reset` | Utility | Clear all data between demo runs |

Every POST response includes `hooksOutput` (execution order of hooks) and `publishedEvents` (events after commit) so the transaction lifecycle is observable in the response body.

## Project Structure

- **Controllers/** — CheckoutController with all scenario and utility endpoints
- **Services/** — OrderService, PaymentService, InventoryService, AuditService, CheckoutService (each demonstrates a transactional pattern)
- **Data/** — CheckoutDbContext (EF Core config)
- **Entities/** — CheckoutOrder, PaymentRecord, InventoryReservation, AuditEntry
- **Exceptions/** — PaymentDeclinedException, InventoryException, NotificationException
- **Infrastructure/** — EnvironmentBootstrapper (Docker), HookOutputCollector, InMemoryEventBus, InMemoryMetricsObserver

## How to Extend

1. **Add a new entity** in `Entities/`
2. **Add a service** in `Services/` with an interface and `[Transactional]` methods
3. **Register in Program.cs** — the scanner auto-discovers the interface
4. **Add an endpoint** in `Controllers/CheckoutController.cs`
5. **Test** — use existing endpoints as reference patterns

## Key Concepts

- **Required:** Inner service joins outer transaction scope (default)
- **RequiresNew:** Opens independent transaction; outer rollback doesn't affect it
- **Suppress:** Runs without transaction; ambient scope is suspended and resumed
- **NoRollbackFor:** Commits even when specified exception types are thrown
- **Hooks:** BeforeCommit, AfterCommit, BeforeRollback, AfterRollback, AfterCompletion
- **Observers:** Track transaction lifecycle (logging, metrics, tracing)
