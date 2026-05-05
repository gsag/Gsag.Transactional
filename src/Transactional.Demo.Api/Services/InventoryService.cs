using Microsoft.EntityFrameworkCore;
using Transactional.Core.Attributes;
using Transactional.Core.Hooks;
using Transactional.Demo.Api.Data;
using Transactional.Demo.Api.Entities;
using Transactional.Demo.Api.Exceptions;
using Transactional.Demo.Api.Infrastructure;

namespace Transactional.Demo.Api.Services;

public class InventoryService : IInventoryService
{
    private readonly CheckoutDbContext _db;
    private readonly ITransactionHooks _hooks;
    private readonly HookOutputCollector _collector;

    public InventoryService(CheckoutDbContext db, ITransactionHooks hooks, HookOutputCollector collector)
    {
        _db = db;
        _hooks = hooks;
        _collector = collector;
    }

    [Transactional]
    public async Task<InventoryReservation> ReserveAsync(int orderId, string productId, int quantity)
    {
        var reservation = new InventoryReservation
        {
            OrderId = orderId,
            ProductId = productId,
            Quantity = quantity,
            ReservedAt = DateTime.UtcNow
        };
        _db.Reservations.Add(reservation);
        await _db.SaveChangesAsync();

        _hooks.AfterCommit(() => _collector.Record($"InventoryService.AfterCommit: reservation for {productId} (qty: {quantity}) confirmed"));
        _hooks.AfterRollback(() => _collector.Record($"InventoryService.AfterRollback: releasing {quantity}x {productId} back to stock"));

        return reservation;
    }

    [Transactional]
    public async Task FailOutOfStockAsync(string productId)
    {
        _hooks.AfterRollback(() => _collector.Record("InventoryService.AfterRollback: out-of-stock check failed — nothing to release"));
        await Task.CompletedTask;
        throw new InventoryException($"Out of stock: {productId} — 0 units available");
    }

    public async Task<IEnumerable<InventoryReservation>> GetAllAsync()
        => await _db.Reservations.AsNoTracking().OrderByDescending(r => r.ReservedAt).ToListAsync();
}
