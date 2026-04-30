using Microsoft.EntityFrameworkCore;
using Transactional.Core.Attributes;
using Transactional.Demo.Api.Data;
using Transactional.Demo.Api.Entities;

namespace Transactional.Demo.Api.Services;

public class OrderService : IOrderService
{
    private readonly AppDbContext _db;

    public OrderService(AppDbContext db) => _db = db;

    [Transactional]
    public async Task<Order> CreateSuccessAsync()
    {
        var order = new Order { CreatedAt = DateTime.UtcNow };
        _db.Orders.Add(order);
        await _db.SaveChangesAsync();
        return order;
    }

    [Transactional]
    public async Task CreateWithRollbackAsync()
    {
        var order = new Order { CreatedAt = DateTime.UtcNow };
        _db.Orders.Add(order);

        // Simulate a business-rule failure before the write reaches the database.
        // The proxy catches this, disposes the TransactionScope without Complete(),
        // and the observer receives OnRollback — no data is persisted.
        //
        // Note: with SQL Server or PostgreSQL (which support System.Transactions
        // enlistment), this throw could be placed AFTER SaveChangesAsync and the
        // INSERT would still be rolled back by TransactionScope.Dispose().
        // EF Core's SQLite provider returns SupportsAmbientTransactions = false,
        // so it doesn't enlist in an ambient TransactionScope.
        await Task.CompletedTask; // preserve async signature
        throw new InvalidOperationException("Simulated failure — transaction rolled back.");
    }

    public async Task<IEnumerable<Order>> GetAllAsync()
        => await _db.Orders.AsNoTracking().ToListAsync();
}
