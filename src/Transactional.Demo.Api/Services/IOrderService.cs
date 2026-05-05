using Transactional.Demo.Api.Entities;

namespace Transactional.Demo.Api.Services;

public interface IOrderService
{
    /// <summary>Creates an order record and commits within the ambient scope.</summary>
    Task<CheckoutOrder> CreateAsync(string scenario, decimal amount, CancellationToken ct = default);

    Task<IReadOnlyList<CheckoutOrder>> GetAllAsync(CancellationToken ct = default);
}
