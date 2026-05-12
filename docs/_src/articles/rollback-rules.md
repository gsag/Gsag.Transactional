# Rollback Rules

By default, any exception thrown by a `[Transactional]` method causes the scope to roll back. `RollbackFor` and `NoRollbackFor` let you fine-tune this behaviour.

---

## Default behaviour

No configuration needed — any exception rolls back:

```csharp
[Transactional]
public async Task PlaceOrderAsync(Order order)
{
    // Any exception here → rollback
}
```

---

## RollbackFor — restrict rollback to specific types

When `RollbackFor` is non-empty, **only** the listed exception types (and their subclasses) trigger rollback. All other exceptions cause the transaction to **commit** even though the exception propagates to the caller.

```csharp
[Transactional(RollbackFor = [typeof(DbException)])]
public async Task PlaceOrderAsync(Order order)
{
    // DbException or any subclass → rollback
    // Any other exception (e.g. ArgumentException) → commits, exception still thrown
}
```

---

## NoRollbackFor — commit despite specific exceptions

When `NoRollbackFor` is set, the listed exception types (and their subclasses) cause the transaction to **commit** even though the exception propagates to the caller. All other exceptions still roll back.

```csharp
[Transactional(NoRollbackFor = [typeof(OperationCanceledException)])]
public async Task PlaceOrderAsync(Order order, CancellationToken ct)
{
    // OperationCanceledException → commits (partial work is preserved)
    // Any other exception → rolls back
}
```

A common reason to use this: a user cancels a long-running request mid-flight. The work completed so far is valid and should be saved; the cancellation is not a data-corruption event.

---

## Precedence

When a type appears in **both** `RollbackFor` and `NoRollbackFor`, `NoRollbackFor` always wins:

```csharp
[Transactional(
    RollbackFor   = [typeof(InvalidOperationException)],
    NoRollbackFor = [typeof(InvalidOperationException)])]
// → commits on InvalidOperationException (NoRollbackFor takes precedence)
```

Subclass resolution applies to both lists: if `IOException` is in `NoRollbackFor` and a `FileNotFoundException` is thrown, the transaction commits (because `FileNotFoundException` is a subclass of `IOException`).

---

## Scenario reference

| Exception thrown | `RollbackFor` | `NoRollbackFor` | Outcome |
|---|---|---|---|
| `InvalidOperationException` | — | — | Rollback (default) |
| `ArgumentException` | `[InvalidOperationException]` | — | **Commit** (not in RollbackFor list) |
| `InvalidOperationException` | `[InvalidOperationException]` | — | Rollback |
| `OperationCanceledException` | — | `[OperationCanceledException]` | **Commit** |
| `TaskCanceledException` | — | `[OperationCanceledException]` | **Commit** (subclass) |
| `InvalidOperationException` | `[InvalidOperationException]` | `[InvalidOperationException]` | **Commit** (NoRollbackFor wins) |
