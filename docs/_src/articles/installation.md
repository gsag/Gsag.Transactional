# Installation

## NuGet

```bash
dotnet add package Gsag.Transactional.Core
```

Or via the Package Manager Console:

```powershell
Install-Package Gsag.Transactional.Core
```

Supported runtimes: **.NET 8** and **.NET 9**.

---

## DI Registration

Call `AddTransactionalServices` in `Program.cs`, passing the assembly that contains your service classes:

```csharp
using Gsag.Transactional.Core.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Scans the assembly for IFoo / FooService pairs and registers proxied transactional services.
builder.Services.AddTransactionalServices(typeof(Program).Assembly);
```

Convention: for each concrete class `FooService` that has at least one `[Transactional]` method, the scanner looks for a matching `IFooService` interface in the same assembly and namespace. If found, it registers the concrete class as `Scoped` and exposes it through a `DispatchProxy` under `IFooService`.

For services that don't follow the `I{ClassName}` convention, register them explicitly:

```csharp
builder.Services.AddTransactionalService<IMyService, MyService>();
```

---

## Logging (optional)

Enable the built-in MEL observer to see every transaction lifecycle event:

```csharp
builder.Services.AddTransactionalLogging();
```

This registers an observer that emits:
- `Debug` — `OnBegin` and `OnCommit`
- `Warning` — `OnRollback`

By default the category is `Gsag.Transactional.Core.Observability.ITransactionObserver`. To enable it in `appsettings.Development.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "Gsag.Transactional.Core": "Debug"
    }
  }
}
```

---

## Verification

Run the application and call any `[Transactional]` method. You should see log lines like:

```
dbug: Gsag.Transactional.Core...ITransactionObserver[0]
      [BEGIN] PlaceOrderAsync | Required | ReadCommitted
dbug: Gsag.Transactional.Core...ITransactionObserver[0]
      [COMMIT] PlaceOrderAsync | 12ms
```

If you see nothing, confirm the log level is set to `Debug` for the `Gsag.Transactional.Core` namespace.
