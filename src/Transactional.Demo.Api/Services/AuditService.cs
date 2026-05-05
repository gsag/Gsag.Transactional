using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Transactional.Core.Attributes;
using Transactional.Demo.Api.Data;
using Transactional.Demo.Api.Entities;
using Transactional.Demo.Api.Infrastructure;

namespace Transactional.Demo.Api.Services;

public class AuditService : IAuditService
{
    private readonly CheckoutDbContext _db;
    private readonly HookOutputCollector _collector;

    public AuditService(CheckoutDbContext db, HookOutputCollector collector)
    {
        _db = db;
        _collector = collector;
    }

    // RequiresNew: opens an independent TransactionScope, suspending any ambient one.
    // This scope commits immediately when WriteAsync completes — regardless of whether
    // the caller's outer transaction later commits or rolls back.
    [Transactional(Propagation = TransactionScopeOption.RequiresNew)]
    public async Task<AuditEntry> WriteAsync(string action, string scenario, bool succeeded)
    {
        _collector.Record("AuditService.WriteAsync: RequiresNew scope opened — independent of outer transaction");

        var entry = new AuditEntry
        {
            Action = action,
            Scenario = scenario,
            Succeeded = succeeded,
            OccurredAt = DateTime.UtcNow
        };
        _db.AuditEntries.Add(entry);
        await _db.SaveChangesAsync();

        _collector.Record($"AuditService.WriteAsync: AuditEntry written and committed in RequiresNew scope — persists regardless of outer outcome");

        return entry;
    }

    public async Task<IEnumerable<AuditEntry>> GetAllAsync()
        => await _db.AuditEntries.AsNoTracking().OrderByDescending(e => e.OccurredAt).ToListAsync();
}
