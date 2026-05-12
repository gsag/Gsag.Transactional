using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Gsag.Transactional.Core.Attributes;
using Gsag.Transactional.Core.Hooks;
using Gsag.Transactional.Demo.Api.Data;
using Gsag.Transactional.Demo.Api.Entities;
using Gsag.Transactional.Demo.Api.Exceptions;
using Gsag.Transactional.Demo.Api.Infrastructure;

namespace Gsag.Transactional.Demo.Api.Services;

public class InventoryService : IInventoryService
{
    private readonly CheckoutDbContext _db;
    private readonly ITransactionHooks _hooks;
    private readonly ILogger<InventoryService> _logger;
    private readonly HookOutputCollector _collector;

    public InventoryService(CheckoutDbContext db, ITransactionHooks hooks, ILogger<InventoryService> logger, HookOutputCollector collector)
    {
        _db = db;
        _hooks = hooks;
        _logger = logger;
        _collector = collector;
    }

    [Transactional]
    public async Task<InventoryReservation> ReserveAsync(int orderId, string productId, int quantity, CancellationToken ct = default)
    {
        var reservation = new InventoryReservation
        {
            OrderId = orderId,
            ProductId = productId,
            Quantity = quantity,
            ReservedAt = DateTimeOffset.UtcNow
        };
        _db.Reservations.Add(reservation);
        await _db.SaveChangesAsync(ct);

        _hooks.AfterCommit(() =>
        {
            _logger.LogDebug("InventoryService.AfterCommit: reservation for {ProductId} (qty: {Quantity}) confirmed", productId, quantity);
            _collector.Record($"InventoryService.AfterCommit: {quantity}x {productId} reserved");
        });
        _hooks.AfterRollback(() =>
        {
            _logger.LogDebug("InventoryService.AfterRollback: releasing {Quantity}x {ProductId} back to stock", quantity, productId);
            _collector.Record($"InventoryService.AfterRollback: {quantity}x {productId} released back to stock");
        });

        return reservation;
    }

    // Synchronous throw — no async work is done before the exception.
    // The proxy still routes this through the async wrapper because the interface return type is Task.
    [Transactional]
    public Task FailOutOfStockAsync(string productId, CancellationToken ct = default)
    {
        _hooks.AfterRollback(() =>
        {
            _logger.LogDebug("InventoryService.AfterRollback: out-of-stock check failed — nothing to release");
            _collector.Record($"InventoryService.AfterRollback: out-of-stock for {productId} — nothing to release");
        });
        throw new InventoryException($"Out of stock: {productId} — 0 units available");
    }

    public async Task<IReadOnlyList<InventoryReservation>> GetAllAsync(CancellationToken ct = default)
        => await _db.Reservations.AsNoTracking().OrderByDescending(r => r.ReservedAt).ToListAsync(ct);
}
