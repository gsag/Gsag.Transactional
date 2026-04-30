using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Transactional.Core.Proxy;
using Transactional.Demo.Api.Data;
using Transactional.Demo.Api.Services;
using Xunit;

namespace Transactional.Tests.Integration;

/// <summary>
/// Proves that [Transactional] actually commits or rolls back against a real SQLite file.
/// Each test class instance gets its own temp DB — deleted in DisposeAsync.
/// </summary>
public class OrderServiceIntegrationTests : IAsyncLifetime
{
    private AppDbContext _db = null!;
    private IOrderService _service = null!;
    private string _dbPath = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"tx_test_{Guid.NewGuid():N}.db");

        _db = BuildContext(_dbPath);
        await _db.Database.EnsureCreatedAsync();

        // Wrap the real service with the transaction proxy — same as DI would do.
        _service = TransactionProxyFactory.Create<IOrderService>(new OrderService(_db));
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        // Force SQLite connection pool to release file handles before deletion.
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    // -------------------------------------------------------------------------

    [Fact]
    public async Task CreateSuccess_CommitsOrder_OrderExistsInDatabase()
    {
        var order = await _service.CreateSuccessAsync();

        var orders = await QueryDbDirectAsync();
        Assert.Single(orders);
        Assert.Equal(order.Id, orders[0].Id);
    }

    [Fact]
    public async Task CreateWithRollback_RollsBack_NothingPersistedInDatabase()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.CreateWithRollbackAsync());

        var orders = await QueryDbDirectAsync();
        Assert.Empty(orders);
    }

    [Fact]
    public async Task TwoSuccessfulCalls_BothOrdersPersisted()
    {
        await _service.CreateSuccessAsync();
        await _service.CreateSuccessAsync();

        var orders = await QueryDbDirectAsync();
        Assert.Equal(2, orders.Count);
    }

    [Fact]
    public async Task SuccessThenRollback_OnlyFirstOrderPersisted()
    {
        await _service.CreateSuccessAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.CreateWithRollbackAsync());

        var orders = await QueryDbDirectAsync();
        Assert.Single(orders);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Queries the database with a fresh DbContext to avoid EF Core's change-tracker
    /// returning stale in-memory entities that were rolled back at the database level.
    /// </summary>
    private async Task<List<Transactional.Demo.Api.Entities.Order>> QueryDbDirectAsync()
    {
        await using var fresh = BuildContext(_dbPath);
        return await fresh.Orders.AsNoTracking().ToListAsync();
    }

    private static AppDbContext BuildContext(string path) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options);
}
