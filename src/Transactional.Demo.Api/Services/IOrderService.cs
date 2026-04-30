using Transactional.Demo.Api.Entities;

namespace Transactional.Demo.Api.Services;

public interface IOrderService
{
    /// <summary>Inserts an order and commits.</summary>
    Task<Order> CreateSuccessAsync();

    /// <summary>Inserts an order then throws — the transaction must roll back.</summary>
    Task CreateWithRollbackAsync();

    /// <summary>Returns all persisted orders.</summary>
    Task<IEnumerable<Order>> GetAllAsync();
}
