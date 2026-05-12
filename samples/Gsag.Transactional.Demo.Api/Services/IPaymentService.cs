using Gsag.Transactional.Demo.Api.Entities;

namespace Gsag.Transactional.Demo.Api.Services;

public interface IPaymentService
{
    /// <summary>Approves payment and saves a record. Registers AfterCommit hook to publish event.</summary>
    Task<PaymentRecord> ProcessAsync(int orderId, decimal amount, CancellationToken ct = default);

    /// <summary>Simulates a card decline. Throws before SaveChanges — triggers rollback.</summary>
    Task FailCardDeclinedAsync(int orderId, decimal amount, CancellationToken ct = default);

    Task<IReadOnlyList<PaymentRecord>> GetAllAsync(CancellationToken ct = default);
}
