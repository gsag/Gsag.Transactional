# Observability (Sample Implementation)

> **Important:** The `Gsag.Transactional.Observability` project is a **sample implementation** demonstrating how to integrate OpenTelemetry with the transactional observer. It is not part of the core library — it is an example you can adapt to your own observability pipeline.

This article describes the sample observability layer included in the demo project. It shows how to wire OpenTelemetry tracing, metrics, and logs to a Grafana LGTM backend using only native .NET primitives.

---

## Architecture

```
┌──────────────────────────────────────────────────────────┐
│                    Your Application                      │
│                                                          │
│  [Transactional] method                                  │
│       │                                                  │
│       ▼                                                  │
│  TransactionProxy                                        │
│       │                                                  │
│       ├── TransactionScope (System.Transactions)         │
│       │                                                  │
│       └── CompositeTransactionObserver                   │
│              ├── LoggingTransactionObserver (MEL)        │
│              ├── OpenTelemetryTransactionObserver ◄──────┤── this sample
│              └── YourCustomObserver                      │
│                                                          │
└──────────────────────────────────────────────────────────┘
                         │
                         ▼ OTLP (gRPC :4317)
              ┌─────────────────────┐
              │   Grafana LGTM      │
              │  ┌─────┐ ┌───────┐  │
              │  │Tempo│ │Prometheus│
              │  └─────┘ └───────┘  │
              │  ┌─────┐            │
              │  │Loki │            │
              │  └─────┘            │
              │  ┌─────────┐        │
              │  │ Grafana │ :3000  │
              │  └─────────┘        │
              └─────────────────────┘
```

---

## Quick Start

### 1. Add the project reference

```xml
<ProjectReference Include="..\Gsag.Transactional.Observability\Gsag.Transactional.Observability.csproj" />
```

### 2. Register the pipeline

```csharp
// Program.cs
builder.Services.AddObservabilityPipeline(builder.Configuration);
```

### 3. Register the observer

```csharp
builder.Services.AddTransactional(b => b
    .AddObservability()  // registers OpenTelemetryTransactionObserver
);
```

### 4. Configure appsettings.json

```json
{
  "Observability": {
    "EnableTracing": true,
    "EnableMetrics": true,
    "EnableLogs": true,
    "Traces": {
      "Protocol": "Grpc",
      "Endpoint": "http://localhost:4317"
    },
    "Metrics": {
      "Protocol": "Grpc",
      "Endpoint": "http://localhost:4317"
    },
    "Logs": {
      "Protocol": "Grpc",
      "Endpoint": "http://localhost:4317"
    }
  }
}
```

---

## What the sample provides

### OpenTelemetry Transaction Observer

The `OpenTelemetryTransactionObserver` implements `ITransactionObserver` and records:

| Signal | Instrument | Description |
|--------|-----------|-------------|
| Trace | `gsag.transactional.transaction` | Span per transaction with outcome, method, propagation tags |
| Metric | `gsag.transactional.transaction.total` | Counter — total transactions started |
| Metric | `gsag.transactional.transaction.committed` | Counter — committed transactions |
| Metric | `gsag.transactional.transaction.rolled_back` | Counter — rolled-back transactions |
| Metric | `gsag.transactional.transaction.duration_ms` | Histogram — transaction duration |

### Health Checks

The sample includes two health checks:

- **PostgreSQL** — opens a connection to verify database accessibility
- **Grafana** — checks `GET /api/health` on the LGTM stack

Both are registered automatically when using `AddObservabilityPipeline(IConfiguration)`.

### Landing Page

A self-contained HTML dashboard served at `/` via `IStartupFilter`:

- Links to Swagger UI, Grafana, and health endpoints
- HTMX-powered live health badges that auto-refresh every 5 seconds
- Dark theme with responsive 2×2 grid layout
- Embedded as a resource in the Observability assembly (no static files)

---

## Configuration reference

### ObservabilityOptions

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `ServiceName` | `string?` | `null` | Logical service name (falls back to entry assembly name) |
| `ServiceVersion` | `string?` | `null` | Service version (falls back to git tag or assembly version) |
| `EnableTracing` | `bool` | `false` | Enable OTLP trace export |
| `EnableMetrics` | `bool` | `false` | Enable OTLP metrics export |
| `EnableLogs` | `bool` | `false` | Enable Serilog → OTLP log export |
| `LandingPagePath` | `string` | `"/"` | Path for the landing page |
| `HealthReadyPath` | `string` | `"/health/ready"` | Path for the ready health check |
| `HealthLivePath` | `string` | `"/health/live"` | Path for the live health check |

### Provider options

Each signal (Traces, Metrics, Logs) has:

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Protocol` | `OtlpExportProtocol` | `Grpc` | Export protocol (Grpc or HttpProtobuf) |
| `Endpoint` | `string` | `"http://localhost:4317"` | OTLP endpoint URI |

---

## Adapting to your stack

This sample is designed to be copied and modified. Common adaptations:

1. **Different backend** — replace OTLP exporter with Jaeger, Zipkin, or vendor-specific exporter
2. **Custom health checks** — add Redis, RabbitMQ, or external service checks in `HealthCheckExtensions`
3. **Custom landing page** — modify `Content/landing-page.html` or replace with your own dashboard
4. **Additional observers** — implement `ITransactionObserver` for custom telemetry (e.g., DataDog, New Relic)
5. **Minimal setup** — use `AddObservabilityPipeline(Action<ObservabilityOptions>)` for code-only configuration without health checks or landing page

---

## Project structure

```
Gsag.Transactional.Observability/
├── ObservabilityOptions.cs                  — Configuration model
├── OpenTelemetryConventions.cs              — Semantic conventions (tags, metrics, activities)
├── ObservabilityServiceMetadata.cs          — Service name/version resolver
├── Observers/
│   └── OpenTelemetryTransactionObserver.cs  — ITransactionObserver → OTel spans + metrics
├── Extensions/
│   ├── ServiceCollectionExtensions.cs       — AddObservabilityPipeline() entry point
│   └── TransactionalBuilderExtensions.cs    — AddObservability() builder extension
├── HealthChecks/
│   └── HealthCheckExtensions.cs             — PostgreSQL + Grafana health checks
├── Startup/
│   └── ObservabilityStartupFilter.cs        — IStartupFilter for landing page + health endpoints
└── Content/
    ├── LandingPageLoader.cs                 — Embedded resource loader
    └── landing-page.html                    — Dashboard HTML
```

---

## Running the demo

```bash
cd samples/Gsag.Transactional.Demo.Api
dotnet run
```

This starts PostgreSQL + Grafana LGTM, opens the landing page, and instruments all `[Transactional]` methods with OpenTelemetry tracing and metrics.

See the Demo API README for full endpoint documentation.
