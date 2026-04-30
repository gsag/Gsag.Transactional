using Transactional.Demo.Api.Entities;

namespace Transactional.Demo.Api.Services;

public interface IOrderService
{
    /// <summary>Inserts an order and commits.</summary>
    Task<Order> CreateSuccessAsync();

    /// <summary>Inserts an order then throws — the transaction must roll back.</summary>
    Task CreateWithRollbackAsync();

    /// <summary>Tracks three orders then throws before saving — all must be discarded.</summary>
    Task CreateBatchWithRollbackAsync();

    /// <summary>Inserts an order in a RequiresNew scope and commits independently.</summary>
    Task<Order> CreateRequiresNewAsync();

    /// <summary>Saves an order then throws OperationCanceledException — NoRollbackFor keeps the commit.</summary>
    Task CreateThenCancelAsync();

    /// <summary>Returns all persisted orders.</summary>
    Task<IEnumerable<Order>> GetAllAsync();
}
