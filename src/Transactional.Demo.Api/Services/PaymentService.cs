using Microsoft.EntityFrameworkCore;
using Transactional.Core.Attributes;
using Transactional.Core.Hooks;
using Transactional.Demo.Api.Data;
using Transactional.Demo.Api.Entities;
using Transactional.Demo.Api.Exceptions;
using Transactional.Demo.Api.Infrastructure;

namespace Transactional.Demo.Api.Services;

public class PaymentService : IPaymentService
{
    private readonly CheckoutDbContext _db;
    private readonly ITransactionHooks _hooks;
    private readonly HookOutputCollector _collector;
    private readonly IEventBus _eventBus;

    public PaymentService(CheckoutDbContext db, ITransactionHooks hooks, HookOutputCollector collector, IEventBus eventBus)
    {
        _db = db;
        _hooks = hooks;
        _collector = collector;
        _eventBus = eventBus;
    }

    [Transactional]
    public async Task<PaymentRecord> ProcessAsync(int orderId, decimal amount, CancellationToken ct = default)
    {
        var record = new PaymentRecord
        {
            OrderId = orderId,
            Amount = amount,
            Status = "approved",
            ProcessedAt = DateTimeOffset.UtcNow
        };
        _db.Payments.Add(record);
        await _db.SaveChangesAsync(ct);

        // Hook fires when the OUTER scope commits — not when ProcessAsync returns.
        // This guarantees the event is only published after the full checkout transaction commits.
        _hooks.AfterCommit(() =>
        {
            _collector.Record("PaymentService.AfterCommit: publishing payment.approved event to event bus");
            _eventBus.Publish("payment.approved", $"orderId={orderId}, amount={amount:F2}");
        });
        _hooks.AfterRollback(() => _collector.Record("PaymentService.AfterRollback: card NOT charged — funds not debited"));

        return record;
    }

    // Synchronous throw — no async work is done before the exception.
    // The proxy still routes this through the async wrapper because the interface return type is Task.
    [Transactional]
    public Task FailCardDeclinedAsync(int orderId, decimal amount, CancellationToken ct = default)
    {
        _hooks.AfterRollback(() => _collector.Record("PaymentService.AfterRollback: decline logged — no charge attempted"));
        throw new PaymentDeclinedException($"Card declined: insufficient funds (order #{orderId}, ${amount:F2})");
    }

    public async Task<IReadOnlyList<PaymentRecord>> GetAllAsync(CancellationToken ct = default)
        => await _db.Payments.AsNoTracking().OrderByDescending(p => p.ProcessedAt).ToListAsync(ct);
}
