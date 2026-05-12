using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Gsag.Transactional.Core.Attributes;
using Gsag.Transactional.Core.Hooks;
using Gsag.Transactional.Demo.Api.Data;
using Gsag.Transactional.Demo.Api.Entities;
using Gsag.Transactional.Demo.Api.Exceptions;
using Gsag.Transactional.Demo.Api.Infrastructure;

namespace Gsag.Transactional.Demo.Api.Services;

public class PaymentService : IPaymentService
{
    private readonly CheckoutDbContext _db;
    private readonly ITransactionHooks _hooks;
    private readonly ILogger<PaymentService> _logger;
    private readonly IEventBus _eventBus;
    private readonly HookOutputCollector _collector;

    public PaymentService(CheckoutDbContext db, ITransactionHooks hooks, ILogger<PaymentService> logger, IEventBus eventBus, HookOutputCollector collector)
    {
        _db = db;
        _hooks = hooks;
        _logger = logger;
        _eventBus = eventBus;
        _collector = collector;
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
            _logger.LogDebug("PaymentService.AfterCommit: publishing payment.approved event to event bus");
            _eventBus.Publish("payment.approved", $"orderId={orderId}, amount={amount:F2}");
            _collector.Record($"PaymentService.AfterCommit: payment.approved published (orderId={orderId}, amount={amount:F2})");
        });
        _hooks.AfterRollback(() =>
        {
            _logger.LogDebug("PaymentService.AfterRollback: card NOT charged — funds not debited");
            _collector.Record($"PaymentService.AfterRollback: card NOT charged — funds not debited (orderId={orderId})");
        });

        return record;
    }

    // Synchronous throw — no async work is done before the exception.
    // The proxy still routes this through the async wrapper because the interface return type is Task.
    [Transactional]
    public Task FailCardDeclinedAsync(int orderId, decimal amount, CancellationToken ct = default)
    {
        _hooks.AfterRollback(() =>
        {
            _logger.LogDebug("PaymentService.AfterRollback: decline logged — no charge attempted");
            _collector.Record($"PaymentService.AfterRollback: decline logged — no charge attempted (orderId={orderId})");
        });
        throw new PaymentDeclinedException($"Card declined: insufficient funds (order #{orderId}, ${amount:F2})");
    }

    public async Task<IReadOnlyList<PaymentRecord>> GetAllAsync(CancellationToken ct = default)
        => await _db.Payments.AsNoTracking().OrderByDescending(p => p.ProcessedAt).ToListAsync(ct);
}
