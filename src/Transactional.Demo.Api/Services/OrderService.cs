using Microsoft.EntityFrameworkCore;
using Transactional.Core.Attributes;
using Transactional.Core.Hooks;
using Transactional.Demo.Api.Data;
using Transactional.Demo.Api.Entities;
using Transactional.Demo.Api.Infrastructure;

namespace Transactional.Demo.Api.Services;

public class OrderService : IOrderService
{
    private readonly CheckoutDbContext _db;
    private readonly ITransactionHooks _hooks;
    private readonly HookOutputCollector _collector;

    public OrderService(CheckoutDbContext db, ITransactionHooks hooks, HookOutputCollector collector)
    {
        _db = db;
        _hooks = hooks;
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

        _hooks.AfterCommit(() => _collector.Record("OrderService.AfterCommit: order record confirmed"));
        _hooks.AfterRollback(() => _collector.Record("OrderService.AfterRollback: order rolled back — record discarded"));
        _hooks.AfterCompletion(() => _collector.Record("OrderService.AfterCompletion: telemetry span closed"));

        return order;
    }

    public async Task<IReadOnlyList<CheckoutOrder>> GetAllAsync(CancellationToken ct = default)
        => await _db.Orders.AsNoTracking().OrderByDescending(o => o.CreatedAt).ToListAsync(ct);
}
