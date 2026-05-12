using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Gsag.Transactional.Core.Attributes;
using Gsag.Transactional.Core.Hooks;
using Gsag.Transactional.Demo.Api.Data;
using Gsag.Transactional.Demo.Api.Entities;
using Gsag.Transactional.Demo.Api.Infrastructure;

namespace Gsag.Transactional.Demo.Api.Services;

public class OrderService : IOrderService
{
    private readonly CheckoutDbContext _db;
    private readonly ITransactionHooks _hooks;
    private readonly ILogger<OrderService> _logger;
    private readonly HookOutputCollector _collector;

    public OrderService(CheckoutDbContext db, ITransactionHooks hooks, ILogger<OrderService> logger, HookOutputCollector collector)
    {
        _db = db;
        _hooks = hooks;
        _logger = logger;
        _collector = collector;
    }

    [Transactional]
    public async Task<CheckoutOrder> CreateAsync(string scenario, decimal amount, CancellationToken ct = default)
    {
        var order = new CheckoutOrder
        {
            Scenario = scenario,
            Status = "created",
            Amount = amount,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _db.Orders.Add(order);
        await _db.SaveChangesAsync(ct);

        _hooks.AfterCommit(() =>
        {
            _logger.LogDebug("OrderService.AfterCommit: order record confirmed");
            _collector.Record($"OrderService.AfterCommit: order #{order.Id} confirmed");
        });
        _hooks.AfterRollback(() =>
        {
            _logger.LogDebug("OrderService.AfterRollback: order rolled back — record discarded");
            _collector.Record($"OrderService.AfterRollback: order #{order.Id} discarded");
        });
        _hooks.AfterCompletion(() => _logger.LogDebug("OrderService.AfterCompletion: telemetry span closed"));

        return order;
    }

    public async Task<IReadOnlyList<CheckoutOrder>> GetAllAsync(CancellationToken ct = default)
        => await _db.Orders.AsNoTracking().OrderByDescending(o => o.CreatedAt).ToListAsync(ct);
}
