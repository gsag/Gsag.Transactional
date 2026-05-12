using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Gsag.Transactional.Core.Attributes;
using Gsag.Transactional.Demo.Api.Data;

namespace Gsag.Transactional.Demo.Api.Services;

public class InventoryReportService : IInventoryReportService
{
    private readonly CheckoutDbContext _db;
    private readonly ILogger<InventoryReportService> _logger;

    public InventoryReportService(CheckoutDbContext db, ILogger<InventoryReportService> logger)
    {
        _db = db;
        _logger = logger;
    }

    // Suppress: if an ambient transaction is active, it is suspended for the duration of this call.
    // Transaction.Current is null inside — this read does not enlist in any transaction.
    // Useful for reads that should not block writers or hold locks within a larger transaction.
    [Transactional(Propagation = TransactionScopeOption.Suppress)]
    public async Task<InventoryReport> ReadAvailableStockAsync(CancellationToken ct = default)
    {
        var isOutsideTransaction = Transaction.Current is null;
        _logger.LogDebug(
            "InventoryReportService: running with Transaction.Current = {Status}",
            isOutsideTransaction ? "null (Suppress confirmed)" : "active (unexpected!)");

        var count = await _db.Reservations.AsNoTracking().CountAsync(ct);

        _logger.LogDebug("InventoryReportService: read {Count} reservation(s) — executed OUTSIDE ambient transaction", count);

        return new InventoryReport(count, "Read outside ambient transaction via [Transactional(Suppress)]");
    }
}
