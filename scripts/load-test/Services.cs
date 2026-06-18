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

class LoadService(ITransactionHooks hooks, IDbContextFactory<LoadTestDbContext> dbFactory, SemaphoreSlim throttle) : ILoadService
{
    [Transactional]
    public async Task InsertAsync()
    {
        await throttle.WaitAsync();
        try
        {
            using var db = dbFactory.CreateDbContext();
            db.Entities.Add(new Entity { Value = 1 });
            await db.SaveChangesAsync();
            hooks.AfterCommit(() => { });
        }
        finally { throttle.Release(); }
    }

    [Transactional]
    public async Task InsertFailAsync()
    {
        await throttle.WaitAsync();
        try
        {
            using var db = dbFactory.CreateDbContext();
            db.Entities.Add(new Entity { Value = 1 });
            await db.SaveChangesAsync();
            hooks.AfterRollback(() => { });
            throw new InvalidOperationException("forced rollback");
        }
        finally { throttle.Release(); }
    }
}

interface IInnerService
{
    Task RunAsync();
}

class InnerService(ITransactionHooks hooks, IDbContextFactory<LoadTestDbContext> dbFactory, SemaphoreSlim throttle) : IInnerService
{
    [Transactional(Propagation = TransactionScopeOption.RequiresNew)]
    public async Task RunAsync()
    {
        await throttle.WaitAsync();
        try
        {
            using var db = dbFactory.CreateDbContext();
            db.Entities.Add(new Entity { Value = 2 });
            await db.SaveChangesAsync();
            hooks.AfterCommit(() => { });
        }
        finally { throttle.Release(); }
    }
}

interface IOuterService
{
    Task RunWithInnerAsync();
}

class OuterService(IInnerService inner, ITransactionHooks hooks, IDbContextFactory<LoadTestDbContext> dbFactory, SemaphoreSlim throttle) : IOuterService
{
    [Transactional]
    public async Task RunWithInnerAsync()
    {
        await throttle.WaitAsync();
        try
        {
            using var db = dbFactory.CreateDbContext();
            db.Entities.Add(new Entity { Value = 1 });
            await db.SaveChangesAsync();
            hooks.AfterCommit(() => { });
            await inner.RunAsync();
        }
        finally { throttle.Release(); }
    }
}

interface IIsolationService
{
    Task UpdateAsync(int taskId, Action onHook);
}

class IsolationService(ITransactionHooks hooks, IDbContextFactory<LoadTestDbContext> dbFactory, SemaphoreSlim throttle) : IIsolationService
{
    [Transactional]
    public async Task UpdateAsync(int taskId, Action onHook)
    {
        await throttle.WaitAsync();
        try
        {
            using var db = dbFactory.CreateDbContext();
            var entity = await db.Entities.FirstOrDefaultAsync() ?? new Entity { Value = 0 };
            if (entity.Id == 0) db.Entities.Add(entity);
            entity.Value += 1;
            await db.SaveChangesAsync();
            hooks.AfterCommit(onHook);
        }
        finally { throttle.Release(); }
    }
}

interface IExceptionService
{
    Task ThrowDuringExecutionAsync();
    Task ThrowInHookAsync();
    Task ThrowCustomExceptionAsync();
}

class ExceptionService(ITransactionHooks hooks, IDbContextFactory<LoadTestDbContext> dbFactory, SemaphoreSlim throttle) : IExceptionService
{
    [Transactional]
    public async Task ThrowDuringExecutionAsync()
    {
        await throttle.WaitAsync();
        try
        {
            using var db = dbFactory.CreateDbContext();
            db.Entities.Add(new Entity { Value = 1 });
            await db.SaveChangesAsync();
            throw new InvalidOperationException("exception during execution");
        }
        finally { throttle.Release(); }
    }

    [Transactional]
    public async Task ThrowInHookAsync()
    {
        await throttle.WaitAsync();
        try
        {
            using var db = dbFactory.CreateDbContext();
            db.Entities.Add(new Entity { Value = 1 });
            await db.SaveChangesAsync();
            hooks.AfterCommit(() => throw new InvalidOperationException("exception in hook"));
        }
        finally { throttle.Release(); }
    }

    [Transactional]
    public async Task ThrowCustomExceptionAsync()
    {
        await throttle.WaitAsync();
        try
        {
            using var db = dbFactory.CreateDbContext();
            db.Entities.Add(new Entity { Value = 1 });
            await db.SaveChangesAsync();
            throw new ApplicationException("custom exception");
        }
        finally { throttle.Release(); }
    }
}

interface IExceptionPropagationService
{
    Task ThrowAndVerifyPropagationAsync(int taskId, int[] observerFired);
}

class ExceptionPropagationService(ITransactionHooks hooks, IDbContextFactory<LoadTestDbContext> dbFactory, SemaphoreSlim throttle) : IExceptionPropagationService
{
    [Transactional]
    public async Task ThrowAndVerifyPropagationAsync(int taskId, int[] observerFired)
    {
        await throttle.WaitAsync();
        try
        {
            using var db = dbFactory.CreateDbContext();
            db.Entities.Add(new Entity { Value = 1 });
            await db.SaveChangesAsync();
            hooks.AfterRollback(() => Interlocked.Increment(ref observerFired[taskId]));
            throw new InvalidOperationException("propagation test");
        }
        finally { throttle.Release(); }
    }
}

interface IInnerFailureService
{
    Task RunAsync();
}

class InnerFailureService(ITransactionHooks hooks, IDbContextFactory<LoadTestDbContext> dbFactory, SemaphoreSlim throttle) : IInnerFailureService
{
    [Transactional(Propagation = TransactionScopeOption.RequiresNew)]
    public async Task RunAsync()
    {
        await throttle.WaitAsync();
        try
        {
            using var db = dbFactory.CreateDbContext();
            db.Entities.Add(new Entity { Value = 2 });
            await db.SaveChangesAsync();
            hooks.AfterRollback(() => { });
            throw new InvalidOperationException("inner transaction failure");
        }
        finally { throttle.Release(); }
    }
}

interface INestedFailureService
{
    Task RunOuterWithFailingInnerAsync();
}

class NestedFailureService(IInnerFailureService inner, ITransactionHooks hooks, IDbContextFactory<LoadTestDbContext> dbFactory, SemaphoreSlim throttle) : INestedFailureService
{
    [Transactional]
    public async Task RunOuterWithFailingInnerAsync()
    {
        await throttle.WaitAsync();
        try
        {
            using var db = dbFactory.CreateDbContext();
            db.Entities.Add(new Entity { Value = 1 });
            await db.SaveChangesAsync();
            hooks.AfterCommit(() => { });
            try { await inner.RunAsync(); }
            catch (InvalidOperationException) { }
        }
        finally { throttle.Release(); }
    }
}

interface IIOSimulationService
{
    Task SimulateIOAsync();
}

class IOSimulationService(ITransactionHooks hooks, IDbContextFactory<LoadTestDbContext> dbFactory, SemaphoreSlim throttle) : IIOSimulationService
{
    [Transactional]
    public async Task SimulateIOAsync()
    {
        await throttle.WaitAsync();
        try
        {
            using var db = dbFactory.CreateDbContext();
            db.Entities.Add(new Entity { Value = 1 });
            await db.SaveChangesAsync();
            var delay = Random.Shared.Next(1, 11);
            await Task.Delay(delay);
            hooks.AfterCommit(() => { });
        }
        finally { throttle.Release(); }
    }
}

interface IHookOrderingService
{
    Task ValidateHookOrderAsync(int taskId, int[] hookFires);
}

class HookOrderingService(ITransactionHooks hooks, IDbContextFactory<LoadTestDbContext> dbFactory, SemaphoreSlim throttle) : IHookOrderingService
{
    [Transactional]
    public async Task ValidateHookOrderAsync(int taskId, int[] hookFires)
    {
        await throttle.WaitAsync();
        try
        {
            using var db = dbFactory.CreateDbContext();
            var baseIdx = taskId * 3;
            db.Entities.Add(new Entity { Value = 1 });
            await db.SaveChangesAsync();
            hooks.AfterCommit(() => Interlocked.Increment(ref hookFires[baseIdx]));
            hooks.AfterCommit(() => Interlocked.Increment(ref hookFires[baseIdx + 1]));
            hooks.AfterCommit(() => Interlocked.Increment(ref hookFires[baseIdx + 2]));
        }
        finally { throttle.Release(); }
    }
}
