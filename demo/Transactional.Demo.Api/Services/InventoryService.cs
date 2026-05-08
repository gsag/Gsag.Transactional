using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Transactional.Core.Attributes;
using Transactional.Core.Hooks;
using Transactional.Demo.Api.Data;
using Transactional.Demo.Api.Entities;
using Transactional.Demo.Api.Exceptions;

namespace Transactional.Demo.Api.Services;

public class InventoryService : IInventoryService
{
    private readonly CheckoutDbContext _db;
    private readonly ITransactionHooks _hooks;
    private readonly ILogger<InventoryService> _logger;

    public InventoryService(CheckoutDbContext db, ITransactionHooks hooks, ILogger<InventoryService> logger)
    {
        _db = db;
        _hooks = hooks;
        _logger = logger;
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

        _hooks.AfterCommit(() => _logger.LogDebug("InventoryService.AfterCommit: reservation for {ProductId} (qty: {Quantity}) confirmed", productId, quantity));
        _hooks.AfterRollback(() => _logger.LogDebug("InventoryService.AfterRollback: releasing {Quantity}x {ProductId} back to stock", quantity, productId));

        return reservation;
    }

    // Synchronous throw — no async work is done before the exception.
    // The proxy still routes this through the async wrapper because the interface return type is Task.
    [Transactional]
    public Task FailOutOfStockAsync(string productId, CancellationToken ct = default)
    {
        _hooks.AfterRollback(() => _logger.LogDebug("InventoryService.AfterRollback: out-of-stock check failed — nothing to release"));
        throw new InventoryException($"Out of stock: {productId} — 0 units available");
    }

    public async Task<IReadOnlyList<InventoryReservation>> GetAllAsync(CancellationToken ct = default)
        => await _db.Reservations.AsNoTracking().OrderByDescending(r => r.ReservedAt).ToListAsync(ct);
}
