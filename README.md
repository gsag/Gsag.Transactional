# csharp-transactional-pattern

Demo solution that implements a custom `@Transactional`-like behavior in C# using **only native .NET features** — no external AOP libraries.

---

## The Problem

Java's Spring Framework lets you annotate a method with `@Transactional` and the framework automatically wraps it in a database transaction — committing on success, rolling back on any exception. There is no built-in equivalent in .NET, but the same behavior can be replicated with `DispatchProxy` and `TransactionScope`.

---

## How It Works

```
┌──────────────────────────────────────────────────────┐
│  DI Container                                        │
│                                                      │
│  IOrderService  ──►  TransactionProxy<IOrderService> │
│                            │                         │
│                            │  intercepts call        │
│                            ▼                         │
│                       [Transactional]?               │
│                       YES ──► TransactionScope       │
│                                    │                 │
│                                    ▼                 │
│                            OrderService.Method()     │
│                                    │                 │
│                         ┌──────────┴──────────┐      │
│                      success               exception │
│                         │                    │       │
│                     Complete()          Dispose()    │
│                       COMMIT            ROLLBACK     │
└──────────────────────────────────────────────────────┘
```

### Key components

| Component | Role |
|---|---|
| `[Transactional]` | Attribute that marks methods for transaction interception |
| `TransactionProxy<T>` | `DispatchProxy` subclass that intercepts every method call |
| `TransactionScopeHelper` | Non-generic owner of commit/rollback/dispose logic and async wrappers |
| `TransactionScope` | Native .NET construct that manages the database transaction |
| `AddTransactionalServices()` | DI extension that auto-wires proxy registration |

### Why `TransactionScope` must be created **before** the method is called

```csharp
// WRONG — EF Core opens the connection before the scope is ambient
var task = InvokeTarget(method, args);   // DB connection opens here (no transaction)
using var scope = CreateScope(attr);     // Too late

// CORRECT — scope is ambient when EF Core opens its connection and enlists
var scope = CreateScope(attr);           // Ambient now
var task = (Task)InvokeTarget(method, args);  // Connection enlists in scope
return WrapAsync(task, scope);           // Wrapper calls Complete() or Dispose()
```

`TransactionScopeAsyncFlowOption.Enabled` propagates the ambient transaction through `ExecutionContext` across every `await` continuation inside the method.

---

## Solution Structure

```
src/
  Transactional.Core/               Pure library, no framework dependencies
    Attributes/
      TransactionalAttribute.cs     IsolationLevel, Propagation, RollbackFor, NoRollbackFor, TimeoutSeconds
    Observability/
      ITransactionLifecycleObserver.cs
      LoggingTransactionObserver.cs
      NullTransactionObserver.cs    Null Object singleton; eliminates null checks on the hot path
    Proxy/
      TransactionProxy.cs           DispatchProxy implementation — routing and caching only
      TransactionScopeHelper.cs     All commit/rollback/dispose logic and async wrappers
      TransactionProxyFactory.cs    Generic and non-generic factory helpers
    Extensions/
      TransactionalExtensions.cs    AddTransactionalServices(Assembly) DI extension
  Transactional.Demo.Api/           ASP.NET Core Web API
    Entities/Order.cs
    Data/AppDbContext.cs            EF Core + SQLite
    Services/IOrderService.cs
    Services/OrderService.cs        Two [Transactional] methods + one plain
    Controllers/OrdersController.cs
    Program.cs
tests/
  Transactional.Tests/
    Unit/TransactionProxyTests.cs        Proxy mechanics, no DB
    Integration/OrderServiceIntegrationTests.cs  Real SQLite commit/rollback
```

---

## `[Transactional]` attribute reference

All properties are optional. The attribute can be placed on the method in the concrete class or in the interface.

### `IsolationLevel`

Controls how the transaction is isolated from concurrent operations.

```csharp
[Transactional(IsolationLevel = IsolationLevel.Serializable)]
public async Task TransferFundsAsync(...) { }
```

| Value | Behaviour |
|---|---|
| `ReadCommitted` *(default)* | Reads only committed data; non-repeatable reads are possible |
| `ReadUncommitted` | Dirty reads allowed; lowest isolation |
| `RepeatableRead` | Rows read cannot change during the transaction; phantom reads are possible |
| `Serializable` | Full isolation; no dirty, non-repeatable, or phantom reads |
| `Snapshot` | Reads a consistent snapshot at the start of the transaction (requires DB support) |

### `Propagation`

Controls how the transaction behaves when a `TransactionScope` is already ambient (e.g., when one transactional method calls another).

```csharp
[Transactional(Propagation = TransactionScopeOption.RequiresNew)]
public async Task AuditLogAsync(...) { }
```

| Value | Behaviour |
|---|---|
| `Required` *(default)* | Join the ambient transaction if one exists; otherwise create a new one |
| `RequiresNew` | Always start a fresh, independent transaction, suspending any ambient one |
| `Suppress` | Execute outside any transaction, suspending the ambient one temporarily |

### `RollbackFor`

By default every exception triggers a rollback. `RollbackFor` narrows that to specific exception types (or their subclasses). All other exceptions will **commit** instead of rolling back.

```csharp
// Only roll back for domain exceptions; let validation errors commit
[Transactional(RollbackFor = [typeof(DomainException)])]
public async Task PlaceOrderAsync(...) { }
```

### `NoRollbackFor`

Commit the transaction even when one of the listed exception types is thrown. `NoRollbackFor` takes precedence over `RollbackFor` when a type appears in both.

```csharp
// Cancelled requests should not undo the transaction
[Transactional(NoRollbackFor = [typeof(OperationCanceledException)])]
public async Task ProcessAsync(CancellationToken ct) { }
```

### `TimeoutSeconds`

Abort and roll back the transaction if it exceeds the given number of seconds. `null` (default) uses the system-configured `TransactionManager.DefaultTimeout`.

```csharp
[Transactional(TimeoutSeconds = 30)]
public async Task LongRunningImportAsync(...) { }
```

### Combining properties

```csharp
[Transactional(
    IsolationLevel   = IsolationLevel.RepeatableRead,
    Propagation      = TransactionScopeOption.RequiresNew,
    TimeoutSeconds   = 60,
    NoRollbackFor    = [typeof(OperationCanceledException)])]
public async Task CriticalOperationAsync(CancellationToken ct) { }
```

---

## Running the API

```bash
cd src/Transactional.Demo.Api
dotnet run
```

Swagger UI: `http://localhost:5000/swagger`

| Endpoint | What happens |
|---|---|
| `POST /orders/success` | Order is inserted and committed — returns 201 |
| `POST /orders/fail` | Order is inserted then an exception is thrown — transaction rolls back, returns 400 |
| `GET /orders` | Returns all persisted orders |

### Verify commit and rollback manually

```bash
# Commit — order persisted
curl -X POST http://localhost:5000/orders/success
curl http://localhost:5000/orders        # → [{id:1,...}]

# Rollback — nothing persisted
curl -X POST http://localhost:5000/orders/fail
curl http://localhost:5000/orders        # → still [{id:1,...}], not 2 items
```

---

## Running the Tests

```bash
dotnet test
```

| Test | What it proves |
|---|---|
| `CreateSuccess_CommitsOrder_*` | `[Transactional]` method commits on success |
| `CreateWithRollback_RollsBack_*` | Exception causes rollback — nothing in DB |
| `TwoSuccessfulCalls_BothOrdersPersisted` | Each call gets its own transaction |
| `SuccessThenRollback_OnlyFirstOrderPersisted` | Rollback is isolated — prior commits survive |
| `Method_WithoutAttribute_PassesThrough` | Proxy is transparent for non-decorated methods |

---

## Design Patterns

### Proxy (Structural)

`TransactionProxy<T>` é o padrão inteiro. O proxy e o objeto real (`_target`) compartilham a mesma interface `T`; chamadores nunca seguram uma referência à classe concreta. Métodos sem `[Transactional]` são repassados de forma transparente via delegate compilado. Métodos decorados recebem o comportamento transacional sem que o chamador saiba.

### Template Method (Behavioral)

Todos os caminhos de execução seguem o mesmo skeleton:

```
OpenScope → Invoke → Commit (sucesso) | Rollback (exceção elegível) | Commit (exceção não elegível) → Dispose
```

`WrapVoidTaskAsync` é a implementação canônica desse skeleton para caminhos async. `WrapGenericTaskAsync<TResult>` e `WrapGenericValueTaskAsync<TResult>` delegam a ele e extraem o resultado em seguida. `HandleSync` re-implementa o mesmo skeleton de forma síncrona — a duplicação estrutural é intencional: misturar código `async` com execução síncrona corrupta o `Transaction.Current` depois que o scope é descartado (confirmado pelo teste `RequiresNew_InsideAmbientScope_SuspendsAndRestoresOuterTransaction`).

### Observer (Behavioral)

`ITransactionLifecycleObserver` desacopla a notificação de eventos (`OnBegin`, `OnCommit`, `OnRollback`) da lógica transacional. `LoggingTransactionObserver` é o observer concreto padrão. Implementações customizadas podem alimentar sistemas de métricas, rastreamento distribuído ou asserções de teste sem tocar no código do serviço.

### Strategy (Behavioral)

`ShouldRollback` encapsula as regras de decisão de rollback parametrizadas pelo `TransactionalAttribute`. Cada método pode carregar uma estratégia diferente via seu atributo. As três estratégias — `NoRollbackFor` vence, `RollbackFor` restringe, padrão faz rollback em qualquer exceção — são estáveis e não justificam extração para uma hierarquia de interfaces.

### Null Object (Behavioral)

`NullTransactionObserver` é um singleton `internal` com implementações no-op dos três métodos do observer. `TransactionProxy<T>` usa `observer ?? NullTransactionObserver.Instance` na inicialização, tornando o campo `_observer` não-nullable e eliminando null-checks no hot path — o mesmo padrão usado pelo BCL em `Stream.Null` e `TextWriter.Null`.

---

## Key Concepts

### `DispatchProxy`

A built-in .NET class that generates a runtime proxy implementing any interface. You override `Invoke()` to intercept every method call. This is how the framework adds behavior (transaction management) without touching the real class.

### `TransactionScope`

`System.Transactions.TransactionScope` creates an ambient transaction. Any database connection opened while the scope is active will automatically enlist in it. When `scope.Complete()` is called before `Dispose()`, the transaction commits; otherwise it rolls back.

### `async` support

The `Invoke()` method is synchronous, but the intercepted methods are `async`. The trick:
1. Create the `TransactionScope` synchronously (so it is ambient)
2. Invoke the method (it starts and returns a `Task` or `ValueTask`)
3. Return a **new** task that `await`s the original one and then calls `Complete()` or `Dispose()`

For `Task<T>` and `ValueTask<T>`, where the result type is unknown at compile time, a private generic helper (`WrapGenericTaskAsync<TResult>` / `WrapGenericValueTaskAsync<TResult>`) is invoked via `MethodInfo.MakeGenericMethod` at runtime to capture and forward the return value. Both wrappers `await` the original task or `ValueTask` directly — no `.AsTask()` allocation is needed.

### Performance caches

`TransactionProxy<T>` maintains two static `ConcurrentDictionary` caches shared across all proxy instances of the same interface:
- **Attribute cache** — `MethodInfo → TransactionalAttribute?` avoids repeated reflection scans. On the first call to a method, the cache checks the concrete method first; if not found, it walks `GetInterfaceMap` to locate the attribute on the matching interface method. Subsequent calls pay no reflection cost.
- **Delegate cache** — `MethodInfo → compiled Func<...>` replaces `MethodInfo.Invoke` with a compiled Expression tree on the hot path

### Selective rollback

C#'s exception filter (`when`) makes selective rollback possible without catching and re-throwing. All async wrappers use a nested try structure to guarantee that `Commit` on the success path is never inside any catch block:

```csharp
try
{
    try
    {
        result = await task.ConfigureAwait(false);
    }
    catch (Exception ex) when (ShouldRollback(ctx.Attr, ex))
    {
        Rollback(ctx, ex);   // rolls back and propagates
        throw;
    }
    catch (Exception)
    {
        Commit(ctx);         // NoRollbackFor path — commit despite exception, then propagate
        throw;
    }
    Commit(ctx);             // success path — outside all catch scopes, no risk of double Complete()
}
finally
{
    ctx.Scope.Dispose();
}
```

The filter runs *before* the stack unwinds. If `ShouldRollback` returns `true`, the first handler fires and rolls back. If `ShouldRollback` returns `false` (e.g. the exception matches `NoRollbackFor`), the filter evaluates to `false`, the first handler is skipped, and the second handler commits. Placing `Commit` on the success path *outside* the inner `catch` blocks prevents double `scope.Complete()` if the observer itself throws.

### Observability

`ITransactionLifecycleObserver` provides a hook into every transaction event:

```csharp
public interface ITransactionLifecycleObserver
{
    void OnBegin(MethodInfo method, TransactionalAttribute attr);
    void OnCommit(MethodInfo method, TimeSpan elapsed);
    void OnRollback(MethodInfo method, Exception exception, TimeSpan elapsed);
}
```

The built-in `LoggingTransactionObserver` writes structured log entries at `DEBUG` (BEGIN/COMMIT) and `WARNING` (ROLLBACK) level. Register it by calling `services.AddTransactionalLogging()` in `Program.cs`. The `OnRollback` entry includes `{ExceptionType}` and `{ExceptionMessage}` as first-class structured properties, independently filterable in Serilog, OpenTelemetry, and similar stores. Custom implementations can feed metrics systems, distributed tracing, or test assertions without touching service code.

### DI auto-discovery convention

`AddTransactionalServices(Assembly)` scans for concrete classes that have at least one `[Transactional]` method (on the class or on its implemented interfaces) and a matching interface named `I{ClassName}` in the same assembly. Both the concrete type and the interface are registered as `Scoped`. The interface resolves as a `TransactionProxy` wrapping the concrete instance; the concrete type is also resolvable directly from the container, so any new constructor dependency it gains is handled automatically by DI without changing the registration.
