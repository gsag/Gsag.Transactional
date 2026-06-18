using System.Collections.Concurrent;
using System.Collections.Immutable;
using Gsag.Transactional.Core.Observability;

namespace LoadTest.Observers;

sealed class ConcurrencyObserver : ITransactionObserver
{
    private int _begin, _commit, _rollback, _complete;
    private long _txnIdCounter;
    private readonly ConcurrentDictionary<long, TransactionLifecycle> _lifetimes = new();
    private readonly AsyncLocal<ImmutableStack<long>> _txnStack = new();

    public int Begin => _begin;
    public int Commit => _commit;
    public int Rollback => _rollback;
    public int Complete => _complete;

    public void Reset()
    {
        _begin = _commit = _rollback = _complete = 0;
        _txnIdCounter = 0;
        _lifetimes.Clear();
    }

    public void OnBegin(TransactionInfo info)
    {
        long id = Interlocked.Increment(ref _txnIdCounter);
        _lifetimes[id] = new TransactionLifecycle { Id = id, Info = info, BeganAt = DateTime.UtcNow };
        var current = _txnStack.Value ?? ImmutableStack<long>.Empty;
        _txnStack.Value = current.Push(id);
        Interlocked.Increment(ref _begin);
    }

    public void OnCommit(TransactionInfo info, TimeSpan elapsed)
    {
        var stack = _txnStack.Value ?? ImmutableStack<long>.Empty;
        long? id = stack.IsEmpty ? null : stack.Peek();
        if (id.HasValue && _lifetimes.TryGetValue(id.Value, out var lifecycle))
        {
            lifecycle.CommitAt = DateTime.UtcNow;
            lifecycle.CommitElapsed = elapsed;
        }
        Interlocked.Increment(ref _commit);
    }

    public void OnRollback(TransactionInfo info, Exception exception, TimeSpan elapsed)
    {
        var stack = _txnStack.Value ?? ImmutableStack<long>.Empty;
        long? id = stack.IsEmpty ? null : stack.Peek();
        if (id.HasValue && _lifetimes.TryGetValue(id.Value, out var lifecycle))
        {
            lifecycle.RollbackAt = DateTime.UtcNow;
            lifecycle.RollbackElapsed = elapsed;
        }
        Interlocked.Increment(ref _rollback);
    }

    public void OnComplete(TransactionInfo info, bool committed, TimeSpan elapsed)
    {
        var stack = _txnStack.Value ?? ImmutableStack<long>.Empty;
        if (stack.IsEmpty)
        {
            Interlocked.Increment(ref _complete);
            return;
        }

        long id = stack.Peek();
        _txnStack.Value = stack.Pop();

        if (_lifetimes.TryGetValue(id, out var lifecycle))
        {
            lifecycle.CompletedAt = DateTime.UtcNow;
            lifecycle.CompleteElapsed = elapsed;
            lifecycle.WasCommitted = committed;
        }
        Interlocked.Increment(ref _complete);
    }

    public ConsistencyCheckResult ValidateConsistency()
    {
        var result = new ConsistencyCheckResult();

        foreach (var kvp in _lifetimes)
        {
            var lifecycle = kvp.Value;

            if (lifecycle.BeganAt == default)
            {
                result.OrphanedTransactions.Add($"TXN {kvp.Key}: No Begin event recorded");
                continue;
            }

            if (lifecycle.CompletedAt == default)
            {
                result.IncompleteTransactions.Add(
                    $"TXN {kvp.Key}: Began but never completed ({lifecycle.Info.MethodName})");
                continue;
            }

            if (lifecycle.CommitAt == default && lifecycle.RollbackAt == default)
            {
                result.IncompleteTransactions.Add(
                    $"TXN {kvp.Key}: Completed but no Commit or Rollback event");
                continue;
            }

            if (lifecycle.CommitAt != default && lifecycle.RollbackAt != default)
            {
                result.InvalidTransitions.Add(
                    $"TXN {kvp.Key}: Both Commit and Rollback events recorded (invalid state)");
            }

            if (lifecycle.WasCommitted && lifecycle.RollbackAt != default)
            {
                result.InvalidTransitions.Add(
                    $"TXN {kvp.Key}: Marked as committed but has Rollback event");
            }

            if (!lifecycle.WasCommitted && lifecycle.CommitAt != default)
            {
                result.InvalidTransitions.Add(
                    $"TXN {kvp.Key}: Marked as rolled back but has Commit event");
            }
        }

        return result;
    }

    private class TransactionLifecycle
    {
        public long Id { get; set; }
        public TransactionInfo Info { get; set; } = null!;
        public DateTime BeganAt { get; set; }
        public DateTime CommitAt { get; set; }
        public DateTime RollbackAt { get; set; }
        public DateTime CompletedAt { get; set; }
        public TimeSpan CommitElapsed { get; set; }
        public TimeSpan RollbackElapsed { get; set; }
        public TimeSpan CompleteElapsed { get; set; }
        public bool WasCommitted { get; set; }
    }
}

record ConsistencyCheckResult
{
    public List<string> OrphanedTransactions { get; } = new();
    public List<string> IncompleteTransactions { get; } = new();
    public List<string> InvalidTransitions { get; } = new();

    public bool IsValid =>
        OrphanedTransactions.Count == 0 &&
        IncompleteTransactions.Count == 0 &&
        InvalidTransitions.Count == 0;

    public string Summary
    {
        get
        {
            if (IsValid) return "✓ All transactions valid";
            var errors = new List<string>();
            if (OrphanedTransactions.Count > 0)
                errors.Add($"Orphaned: {OrphanedTransactions.Count}");
            if (IncompleteTransactions.Count > 0)
                errors.Add($"Incomplete: {IncompleteTransactions.Count}");
            if (InvalidTransitions.Count > 0)
                errors.Add($"Invalid: {InvalidTransitions.Count}");
            return $"✗ {string.Join(", ", errors)}";
        }
    }
}
