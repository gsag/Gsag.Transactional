# Installation

## NuGet

```bash
dotnet add package Gsag.Transactional.Core
```

Or via the Package Manager Console:

```powershell
Install-Package Gsag.Transactional.Core
```

## DI Registration

Call `AddTransactional` in `Program.cs`:

```csharp
using Gsag.Transactional.Core.Extensions;

var builder = WebApplication.CreateBuilder(args);

// The calling assembly is automatically scanned for IFoo / FooService pairs.
builder.Services.AddTransactional(b => b
    .AddLogging()  // optional
);
```

By default, the **calling assembly is automatically scanned** for service classes. Convention: for each concrete class `FooService` that has at least one `[Transactional]` method, the scanner looks for a matching `IFooService` interface in the same assembly and namespace. If found, it registers the concrete class as `Scoped` and exposes it through a `DispatchProxy` under `IFooService`.

To scan a **different assembly**, use `ScanAssembly()`. Note: calling `ScanAssembly()` **overwrites the default behavior** — only the specified assembly is scanned:

```csharp
// Only SomeService.Assembly is scanned; calling assembly is NOT scanned
builder.Services.AddTransactional(b => b
    .ScanAssembly(typeof(SomeService).Assembly)
    .AddLogging()
);
```

To scan **multiple assemblies**, call `ScanAssembly()` multiple times:

```csharp
builder.Services.AddTransactional(b => b
    .ScanAssembly(typeof(SomeService).Assembly)
    .ScanAssembly(typeof(OtherService).Assembly)
    .AddLogging()
);
```

For services that don't follow the `I{ClassName}` convention, register them explicitly:

```csharp
builder.Services.AddTransactional(b => b
    .AddService<IMyService, MyService>()
);
```

---

## Logging (optional)

Enable the built-in MEL observer to see every transaction lifecycle event:

```csharp
builder.Services.AddTransactional(b => b.AddLogging());
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
