using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Transactional.Core.Attributes;
using Transactional.Demo.Api.Data;
using Transactional.Demo.Api.Infrastructure;

namespace Transactional.Demo.Api.Services;

public class InventoryReportService : IInventoryReportService
{
    private readonly CheckoutDbContext _db;
    private readonly HookOutputCollector _collector;

    public InventoryReportService(CheckoutDbContext db, HookOutputCollector collector)
    {
        _db = db;
        _collector = collector;
    }

    // Suppress: if an ambient transaction is active, it is suspended for the duration of this call.
    // Transaction.Current is null inside — this read does not enlist in any transaction.
    // Useful for reads that should not block writers or hold locks within a larger transaction.
    [Transactional(Propagation = TransactionScopeOption.Suppress)]
    public async Task<InventoryReport> ReadAvailableStockAsync()
    {
        var isOutsideTransaction = Transaction.Current is null;
        _collector.Record($"InventoryReportService: running with Transaction.Current = {(isOutsideTransaction ? "null ✓ (Suppress confirmed)" : "active (unexpected!)")}");

        var count = await _db.Reservations.AsNoTracking().CountAsync();

        _collector.Record($"InventoryReportService: read {count} reservation(s) — executed OUTSIDE ambient transaction");

        return new InventoryReport(count, "Read outside ambient transaction via [Transactional(Suppress)]");
    }
}
