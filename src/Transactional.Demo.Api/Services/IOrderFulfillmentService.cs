using Transactional.Demo.Api.Entities;

namespace Transactional.Demo.Api.Services;

public interface IOrderFulfillmentService
{
    /// <summary>
    /// Opens a Required outer scope, then delegates to CreateRequiresNewAsync (RequiresNew).
    /// Both scopes complete — demonstrates nested transactions with independent scopes.
    /// </summary>
    Task<Order> FulfillAsync();

    /// <summary>
    /// Opens a Required outer scope, calls CreateRequiresNewAsync (RequiresNew inner commits),
    /// then throws. The outer scope is abandoned — inner data persists, outer has no write.
    /// On SQL Server / PostgreSQL the inner write survives the outer rollback via RequiresNew.
    /// </summary>
    Task FulfillThenFailAsync();
}
