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
    public async Task<PaymentRecord> ProcessAsync(int orderId, decimal amount)
    {
        var record = new PaymentRecord
        {
            OrderId = orderId,
            Amount = amount,
            Status = "approved",
            ProcessedAt = DateTime.UtcNow
        };
        _db.Payments.Add(record);
        await _db.SaveChangesAsync();

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

    [Transactional]
    public async Task FailCardDeclinedAsync(int orderId, decimal amount)
    {
        _hooks.AfterRollback(() => _collector.Record("PaymentService.AfterRollback: decline logged — no charge attempted"));
        await Task.CompletedTask;
        throw new PaymentDeclinedException($"Card declined: insufficient funds (order #{orderId}, ${amount:F2})");
    }

    public async Task<IEnumerable<PaymentRecord>> GetAllAsync()
        => await _db.Payments.AsNoTracking().OrderByDescending(p => p.ProcessedAt).ToListAsync();
}
