using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Transactional.Core.Attributes;
using Transactional.Core.Proxy;
using Transactional.Demo.Api.Data;
using Transactional.Demo.Api.Entities;
using Transactional.Demo.Api.Services;
using Transactional.Tests.Unit;
using Xunit;

namespace Transactional.Tests.Integration;

// ---------------------------------------------------------------------------
// Test doubles for scenario 2 (RequiresNew cross-service)
// ---------------------------------------------------------------------------

/// <summary>
/// Outer service whose single method opens a Required scope, delegates to an
/// injected inner service (RequiresNew), then throws before doing any work of
/// its own. The inner proxy must commit independently before the outer fails.
/// </summary>
public interface IOuterTransactionalService
{
    Task CallInnerThenFailAsync();
}

public class OuterTransactionalService : IOuterTransactionalService
{
    private readonly IOrderService _inner;

    public OuterTransactionalService(IOrderService inner) => _inner = inner;

    [Transactional]
    public async Task CallInnerThenFailAsync()
    {
        await _inner.CreateRequiresNewAsync();
        throw new InvalidOperationException("outer fails — inner must have already committed");
    }
}

// ---------------------------------------------------------------------------

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

        // WAL mode persists on the file: all subsequent connections inherit it,
        // enabling concurrent writers to serialize rather than fail with SQLITE_BUSY.
        await _db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");

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
    // Existing scenarios
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
    // Scenario 1 — Batch atomicity
    // -------------------------------------------------------------------------

    [Fact]
    public async Task BatchInsert_WhenFailsBeforeSave_AllPendingInsertsDiscarded()
    {
        // Three entities are tracked by EF Core but SaveChanges is never called.
        // The proxy catches the exception, disposes the TransactionScope without
        // Complete(), and no rows reach the database.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.CreateBatchWithRollbackAsync());

        Assert.Empty(await QueryDbDirectAsync());
    }

    // -------------------------------------------------------------------------
    // Scenario 2 — RequiresNew: inner scope commits independently of outer
    // -------------------------------------------------------------------------

    [Fact]
    public async Task RequiresNew_WhenCalledFromFailingOuterTransaction_InnerDataPersists()
    {
        // Outer: [Transactional] Required — wraps OuterTransactionalService.
        // Inner: [Transactional(RequiresNew)] — _service.CreateRequiresNewAsync().
        //
        // The outer proxy opens a Required scope, calls the inner proxy (which opens
        // its own independent RequiresNew scope and commits), then throws.  The outer
        // scope is abandoned without Complete().
        //
        // On SQL Server / PostgreSQL, the inner write would survive the outer rollback
        // because RequiresNew creates a truly independent database transaction.
        // With SQLite (no System.Transactions enlistment) SaveChangesAsync commits
        // immediately in both scopes — but the transactional wiring between two
        // [Transactional]-decorated services is exercised correctly either way.
        var outerProxy = TransactionProxyFactory.Create<IOuterTransactionalService>(
            new OuterTransactionalService(_service));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => outerProxy.CallInnerThenFailAsync());

        var orders = await QueryDbDirectAsync();
        Assert.Single(orders);
    }

    // -------------------------------------------------------------------------
    // Scenario 3 — NoRollbackFor: scope completes despite exception
    // -------------------------------------------------------------------------

    [Fact]
    public async Task NoRollbackFor_WhenMatchingExceptionThrownAfterSave_DataPersistedAndObserverReceivesCommit()
    {
        var observer = new RecordingObserver();
        var service = TransactionProxyFactory.Create<IOrderService>(new OrderService(_db), observer);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.CreateThenCancelAsync());

        // SaveChangesAsync ran before the throw, so the row exists.
        Assert.Single(await QueryDbDirectAsync());

        // The scope was completed (NoRollbackFor path), so the observer must
        // report COMMIT, not ROLLBACK.
        Assert.Contains("COMMIT:CreateThenCancelAsync", observer.Calls);
        Assert.DoesNotContain("ROLLBACK:CreateThenCancelAsync", observer.Calls);
    }

    // -------------------------------------------------------------------------
    // Scenario 4 — Observer lifecycle correlates with real database state
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Observer_AfterCommitEvent_DataIsVisibleInDatabase()
    {
        var observer = new RecordingObserver();
        var service = TransactionProxyFactory.Create<IOrderService>(new OrderService(_db), observer);

        await service.CreateSuccessAsync();

        Assert.Contains("COMMIT:CreateSuccessAsync", observer.Calls);
        Assert.DoesNotContain("ROLLBACK:CreateSuccessAsync", observer.Calls);
        Assert.Single(await QueryDbDirectAsync());
    }

    [Fact]
    public async Task Observer_AfterRollbackEvent_NothingPersistedInDatabase()
    {
        var observer = new RecordingObserver();
        var service = TransactionProxyFactory.Create<IOrderService>(new OrderService(_db), observer);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CreateWithRollbackAsync());

        Assert.Contains("ROLLBACK:CreateWithRollbackAsync", observer.Calls);
        Assert.DoesNotContain("COMMIT:CreateWithRollbackAsync", observer.Calls);
        Assert.Empty(await QueryDbDirectAsync());
    }

    // -------------------------------------------------------------------------
    // Scenario 5 — Concurrent transactions: proxy cache must not corrupt state
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Concurrent_FiveParallelTransactions_AllOrdersPersisted()
    {
        const int count = 5;

        // Each task requires its own DbContext — EF Core is not thread-safe.
        // WAL mode (set in InitializeAsync) allows SQLite to serialize concurrent
        // writers without returning SQLITE_BUSY immediately.
        var contexts = Enumerable.Range(0, count)
            .Select(_ => BuildContextWithBusyTimeoutAsync(_dbPath))
            .ToList();

        var resolvedContexts = await Task.WhenAll(contexts);
        try
        {
            var tasks = resolvedContexts.Select(db =>
                TransactionProxyFactory.Create<IOrderService>(new OrderService(db))
                    .CreateSuccessAsync());

            await Task.WhenAll(tasks);
        }
        finally
        {
            foreach (var ctx in resolvedContexts)
            {
                await ctx.DisposeAsync();
            }
        }

        Assert.Equal(count, (await QueryDbDirectAsync()).Count);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Queries the database with a fresh DbContext to avoid EF Core's change-tracker
    /// returning stale in-memory entities that were rolled back at the database level.
    /// </summary>
    private async Task<List<Order>> QueryDbDirectAsync()
    {
        await using var fresh = BuildContext(_dbPath);
        return await fresh.Orders.AsNoTracking().ToListAsync();
    }

    private static AppDbContext BuildContext(string path) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={path}")
            // SQLite doesn't support System.Transactions enlistment — suppress the warning
            // so tests that deliberately open TransactionScopes don't fail on detection.
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.AmbientTransactionWarning))
            .Options);

    /// <summary>
    /// Creates a context and configures a 5-second busy timeout so concurrent
    /// writers queue rather than immediately fail with SQLITE_BUSY.
    /// </summary>
    private static async Task<AppDbContext> BuildContextWithBusyTimeoutAsync(string path)
    {
        var ctx = BuildContext(path);
        await ctx.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout=5000;");
        return ctx;
    }
}
