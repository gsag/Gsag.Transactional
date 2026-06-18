using System.Transactions;
using Gsag.Transactional.Core.Attributes;
using Gsag.Transactional.Core.Hooks;
using LoadTest.Data;
using Microsoft.EntityFrameworkCore;

namespace LoadTest.Services;

interface ILoadService
{
    Task InsertAsync();
    Task InsertFailAsync();
}

class LoadService(ITransactionHooks hooks, IDbContextFactory<LoadTestDbContext> dbFactory) : ILoadService
{
    [Transactional]
    public async Task InsertAsync()
    {
        using var db = dbFactory.CreateDbContext();
        db.Entities.Add(new Entity { Value = 1 });
        await db.SaveChangesAsync();
        hooks.AfterCommit(() => { });
    }

    [Transactional]
    public async Task InsertFailAsync()
    {
        using var db = dbFactory.CreateDbContext();
        db.Entities.Add(new Entity { Value = 1 });
        await db.SaveChangesAsync();
        hooks.AfterRollback(() => { });
        throw new InvalidOperationException("forced rollback");
    }
}

interface IInnerService
{
    Task RunAsync();
}

class InnerService(ITransactionHooks hooks, IDbContextFactory<LoadTestDbContext> dbFactory) : IInnerService
{
    [Transactional(Propagation = TransactionScopeOption.RequiresNew)]
    public async Task RunAsync()
    {
        using var db = dbFactory.CreateDbContext();
        db.Entities.Add(new Entity { Value = 2 });
        await db.SaveChangesAsync();
        hooks.AfterCommit(() => { });
    }
}

interface IOuterService
{
    Task RunWithInnerAsync();
}

class OuterService(IInnerService inner, ITransactionHooks hooks, IDbContextFactory<LoadTestDbContext> dbFactory) : IOuterService
{
    [Transactional]
    public async Task RunWithInnerAsync()
    {
        using var db = dbFactory.CreateDbContext();
        db.Entities.Add(new Entity { Value = 1 });
        await db.SaveChangesAsync();
        hooks.AfterCommit(() => { });
        await inner.RunAsync();
    }
}

interface IIsolationService
{
    Task UpdateAsync(int taskId, Action onHook);
}

class IsolationService(ITransactionHooks hooks, IDbContextFactory<LoadTestDbContext> dbFactory) : IIsolationService
{
    [Transactional]
    public async Task UpdateAsync(int taskId, Action onHook)
    {
        using var db = dbFactory.CreateDbContext();
        var entity = await db.Entities.FirstOrDefaultAsync() ?? new Entity { Value = 0 };
        if (entity.Id == 0) db.Entities.Add(entity);
        entity.Value += 1;
        await db.SaveChangesAsync();
        hooks.AfterCommit(onHook);
    }
}

interface IExceptionService
{
    Task ThrowDuringExecutionAsync();
    Task ThrowInHookAsync();
    Task ThrowCustomExceptionAsync();
}

class ExceptionService(ITransactionHooks hooks, IDbContextFactory<LoadTestDbContext> dbFactory) : IExceptionService
{
    [Transactional]
    public async Task ThrowDuringExecutionAsync()
    {
        using var db = dbFactory.CreateDbContext();
        db.Entities.Add(new Entity { Value = 1 });
        await db.SaveChangesAsync();
        throw new InvalidOperationException("exception during execution");
    }

    [Transactional]
    public async Task ThrowInHookAsync()
    {
        using var db = dbFactory.CreateDbContext();
        db.Entities.Add(new Entity { Value = 1 });
        await db.SaveChangesAsync();
        hooks.AfterCommit(() => throw new InvalidOperationException("exception in hook"));
    }

    [Transactional]
    public async Task ThrowCustomExceptionAsync()
    {
        using var db = dbFactory.CreateDbContext();
        db.Entities.Add(new Entity { Value = 1 });
        await db.SaveChangesAsync();
        throw new ApplicationException("custom exception");
    }
}

interface IExceptionPropagationService
{
    Task ThrowAndVerifyPropagationAsync(int taskId, int[] observerFired);
}

class ExceptionPropagationService(ITransactionHooks hooks, IDbContextFactory<LoadTestDbContext> dbFactory) : IExceptionPropagationService
{
    [Transactional]
    public async Task ThrowAndVerifyPropagationAsync(int taskId, int[] observerFired)
    {
        using var db = dbFactory.CreateDbContext();
        db.Entities.Add(new Entity { Value = 1 });
        await db.SaveChangesAsync();
        hooks.AfterRollback(() => Interlocked.Increment(ref observerFired[taskId]));
        throw new InvalidOperationException("propagation test");
    }
}

interface IInnerFailureService
{
    Task RunAsync();
}

class InnerFailureService(ITransactionHooks hooks, IDbContextFactory<LoadTestDbContext> dbFactory) : IInnerFailureService
{
    [Transactional(Propagation = TransactionScopeOption.RequiresNew)]
    public async Task RunAsync()
    {
        using var db = dbFactory.CreateDbContext();
        db.Entities.Add(new Entity { Value = 2 });
        await db.SaveChangesAsync();
        hooks.AfterRollback(() => { });
        throw new InvalidOperationException("inner transaction failure");
    }
}

interface INestedFailureService
{
    Task RunOuterWithFailingInnerAsync();
}

class NestedFailureService(IInnerFailureService inner, ITransactionHooks hooks, IDbContextFactory<LoadTestDbContext> dbFactory) : INestedFailureService
{
    [Transactional]
    public async Task RunOuterWithFailingInnerAsync()
    {
        using var db = dbFactory.CreateDbContext();
        db.Entities.Add(new Entity { Value = 1 });
        await db.SaveChangesAsync();
        hooks.AfterCommit(() => { });
        try { await inner.RunAsync(); }
        catch (InvalidOperationException) { }
    }
}

interface IIOSimulationService
{
    Task SimulateIOAsync();
}

class IOSimulationService(ITransactionHooks hooks, IDbContextFactory<LoadTestDbContext> dbFactory) : IIOSimulationService
{
    [Transactional]
    public async Task SimulateIOAsync()
    {
        using var db = dbFactory.CreateDbContext();
        db.Entities.Add(new Entity { Value = 1 });
        await db.SaveChangesAsync();
        var delay = Random.Shared.Next(1, 11);
        await Task.Delay(delay);
        hooks.AfterCommit(() => { });
    }
}

interface IHookOrderingService
{
    Task ValidateHookOrderAsync(int taskId, int[] hookFires);
}

class HookOrderingService(ITransactionHooks hooks, IDbContextFactory<LoadTestDbContext> dbFactory) : IHookOrderingService
{
    [Transactional]
    public async Task ValidateHookOrderAsync(int taskId, int[] hookFires)
    {
        using var db = dbFactory.CreateDbContext();
        var baseIdx = taskId * 3;
        db.Entities.Add(new Entity { Value = 1 });
        await db.SaveChangesAsync();
        hooks.AfterCommit(() => Interlocked.Increment(ref hookFires[baseIdx]));
        hooks.AfterCommit(() => Interlocked.Increment(ref hookFires[baseIdx + 1]));
        hooks.AfterCommit(() => Interlocked.Increment(ref hookFires[baseIdx + 2]));
    }
}
