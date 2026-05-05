using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Transactional.Core.Attributes;
using Transactional.Core.Hooks;
using Transactional.Demo.Api.Data;
using Transactional.Demo.Api.Entities;
using Transactional.Demo.Api.Infrastructure;

namespace Transactional.Demo.Api.Services;

public class AuditService : IAuditService
{
    private readonly CheckoutDbContext _db;
    private readonly ITransactionHooks _hooks;
    private readonly HookOutputCollector _collector;

    public AuditService(CheckoutDbContext db, ITransactionHooks hooks, HookOutputCollector collector)
    {
        _db = db;
        _hooks = hooks;
        _collector = collector;
    }

    // RequiresNew: opens an independent TransactionScope, suspending any ambient one.
    // This scope commits immediately when WriteAsync completes — regardless of whether
    // the caller's outer transaction later commits or rolls back.
    // The AfterCommit hook below fires at THIS scope's commit, not the outer scope's.
    [Transactional(Propagation = TransactionScopeOption.RequiresNew)]
    public async Task<AuditEntry> WriteAsync(string action, string scenario, bool succeeded, CancellationToken ct = default)
    {
        _collector.Record("AuditService.WriteAsync: RequiresNew scope opened — independent of outer transaction");

        var entry = new AuditEntry
        {
            Action = action,
            Scenario = scenario,
            Succeeded = succeeded,
            OccurredAt = DateTimeOffset.UtcNow
        };
        _db.AuditEntries.Add(entry);
        await _db.SaveChangesAsync(ct);

        // This hook fires at the RequiresNew scope commit — before WriteAsync even returns to the caller.
        // It does NOT wait for the outer scope; RequiresNew has its own independent hook collection.
        _hooks.AfterCommit(() => _collector.Record("AuditService.AfterCommit (RequiresNew): audit entry committed — hook fired at THIS scope's commit, independent of outer"));

        _collector.Record($"AuditService.WriteAsync: AuditEntry written and committed in RequiresNew scope — persists regardless of outer outcome");

        return entry;
    }

    public async Task<IReadOnlyList<AuditEntry>> GetAllAsync(CancellationToken ct = default)
        => await _db.AuditEntries.AsNoTracking().OrderByDescending(e => e.OccurredAt).ToListAsync(ct);
}
