using Gsag.Transactional.Demo.Api.Entities;

namespace Gsag.Transactional.Demo.Api.Services;

public interface IInventoryService
{
    /// <summary>Reserves stock for the given order. Commits within the ambient scope.</summary>
    Task<InventoryReservation> ReserveAsync(int orderId, string productId, int quantity, CancellationToken ct = default);

    /// <summary>Simulates an out-of-stock condition. Throws before SaveChanges — triggers rollback.</summary>
    Task FailOutOfStockAsync(string productId, CancellationToken ct = default);

    Task<IReadOnlyList<InventoryReservation>> GetAllAsync(CancellationToken ct = default);
}
