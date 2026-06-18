using System.Transactions;
using Gsag.Transactional.Core.Attributes;
using Gsag.Transactional.Core.Hooks;
using LoadTest.Data;
using Microsoft.EntityFrameworkCore;

namespace LoadTest.Services;

interface ILoadService
{
    Task InsertAccountAsync();
    Task InsertAccountFailAsync();
}

class LoadService(ITransactionHooks hooks, IDbContextFactory<LoadTestDbContext> dbFactory) : ILoadService
{
    [Transactional]
    public async Task InsertAccountAsync()
    {
        using var db = dbFactory.CreateDbContext();
        var account = new LoadTestAccount { Name = Guid.NewGuid().ToString(), Balance = 1000 };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        hooks.AfterCommit(() => { });
    }

    [Transactional]
    public async Task InsertAccountFailAsync()
    {
        using var db = dbFactory.CreateDbContext();
        var account = new LoadTestAccount { Name = Guid.NewGuid().ToString(), Balance = 1000 };
        db.Accounts.Add(account);
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
        var account = new LoadTestAccount { Name = Guid.NewGuid().ToString(), Balance = 500 };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        hooks.AfterCommit(() => { });
    }
}

interface IOuterService
{
    Task RunWithInnerBankAsync();
}

class OuterService(ITransactionHooks hooks, IInnerService inner, IDbContextFactory<LoadTestDbContext> dbFactory) : IOuterService
{
    [Transactional]
    public async Task RunWithInnerBankAsync()
    {
        using var db = dbFactory.CreateDbContext();
        var account = new LoadTestAccount { Name = Guid.NewGuid().ToString(), Balance = 1000 };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        hooks.AfterCommit(() => { });
        await inner.RunAsync();
    }
}

interface IIsolationService
{
    Task UpdateAccountAsync(int taskId, Action onCommit);
}

class IsolationService(ITransactionHooks hooks, IDbContextFactory<LoadTestDbContext> dbFactory) : IIsolationService
{
    [Transactional]
    public async Task UpdateAccountAsync(int taskId, Action onCommit)
    {
        using var db = dbFactory.CreateDbContext();
        var account = new LoadTestAccount { Name = $"account-{taskId}", Balance = 1000m };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        hooks.AfterCommit(onCommit);
    }
}

interface IExceptionService
{
    Task ThrowDuringExecutionBankAsync();
    Task ThrowInHookBankAsync();
    Task ThrowCustomExceptionBankAsync();
}

class ExceptionService(ITransactionHooks hooks, IDbContextFactory<LoadTestDbContext> dbFactory) : IExceptionService
{
    [Transactional]
    public async Task ThrowDuringExecutionBankAsync()
    {
        using var db = dbFactory.CreateDbContext();
        var account = new LoadTestAccount { Name = Guid.NewGuid().ToString(), Balance = 1000 };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        hooks.AfterCommit(() => { });
        throw new InvalidOperationException("Exception during transaction execution");
    }

    [Transactional]
    public async Task ThrowInHookBankAsync()
    {
        using var db = dbFactory.CreateDbContext();
        var account = new LoadTestAccount { Name = Guid.NewGuid().ToString(), Balance = 1000 };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        hooks.AfterCommit(() => throw new ArgumentException("Exception in AfterCommit hook"));
    }

    [Transactional]
    public async Task ThrowCustomExceptionBankAsync()
    {
        using var db = dbFactory.CreateDbContext();
        var account = new LoadTestAccount { Name = Guid.NewGuid().ToString(), Balance = 1000 };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        hooks.AfterCommit(() => { });
        throw new TimeoutException("Custom exception during transaction");
    }
}

interface IExceptionPropagationService
{
    Task ThrowAndVerifyPropagationBankAsync(int taskId, int[] rollbackObserverFired);
}

class ExceptionPropagationService(ITransactionHooks hooks, IDbContextFactory<LoadTestDbContext> dbFactory) : IExceptionPropagationService
{
    [Transactional]
    public async Task ThrowAndVerifyPropagationBankAsync(int taskId, int[] rollbackObserverFired)
    {
        using var db = dbFactory.CreateDbContext();
        var account = new LoadTestAccount { Name = $"prop-{taskId}", Balance = 1000 };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        hooks.AfterRollback(() =>
        {
            Interlocked.Increment(ref rollbackObserverFired[taskId]);
        });
        throw new InvalidOperationException($"Task {taskId}: Exception for propagation test");
    }
}

interface INestedFailureService
{
    Task RunOuterWithFailingInnerBankAsync();
}

interface IInnerFailureService
{
    Task RunAndFailAsync();
}

class InnerFailureService(ITransactionHooks hooks, IDbContextFactory<LoadTestDbContext> dbFactory) : IInnerFailureService
{
    [Transactional(Propagation = TransactionScopeOption.RequiresNew)]
    public async Task RunAndFailAsync()
    {
        using var db = dbFactory.CreateDbContext();
        var account = new LoadTestAccount { Name = Guid.NewGuid().ToString(), Balance = 500 };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        hooks.AfterRollback(() => { });
        throw new InvalidOperationException("Inner transaction failed intentionally");
    }
}

class NestedFailureService(ITransactionHooks hooks, IInnerFailureService inner, IDbContextFactory<LoadTestDbContext> dbFactory) : INestedFailureService
{
    [Transactional]
    public async Task RunOuterWithFailingInnerBankAsync()
    {
        using var db = dbFactory.CreateDbContext();
        var account = new LoadTestAccount { Name = Guid.NewGuid().ToString(), Balance = 1000 };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        hooks.AfterCommit(() => { });
        try
        {
            await inner.RunAndFailAsync();
        }
        catch (InvalidOperationException)
        {
        }
    }
}

interface IIOSimulationService
{
    Task SimulateIOWithBankAsync();
}

class IOSimulationService(ITransactionHooks hooks, IDbContextFactory<LoadTestDbContext> dbFactory) : IIOSimulationService
{
    private static readonly Random _random = new();

    [Transactional]
    public async Task SimulateIOWithBankAsync()
    {
        using var db = dbFactory.CreateDbContext();
        var account = new LoadTestAccount { Name = Guid.NewGuid().ToString(), Balance = 1000 };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        int delayMs = _random.Next(1, 11);
        await Task.Delay(delayMs);

        await db.Accounts.Where(a => a.Name == account.Name).FirstOrDefaultAsync();

        hooks.AfterCommit(() => { });
    }
}

interface IHookOrderingService
{
    Task ValidateHookOrderBankAsync(int taskId, int[] hookFireCount);
}

class HookOrderingService(ITransactionHooks hooks, IDbContextFactory<LoadTestDbContext> dbFactory) : IHookOrderingService
{
    [Transactional]
    public async Task ValidateHookOrderBankAsync(int taskId, int[] hookFireCount)
    {
        using var db = dbFactory.CreateDbContext();
        var account = new LoadTestAccount { Name = $"hook-{taskId}", Balance = 1000 };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        int baseIndex = taskId * 3;
        hooks.AfterCommit(() => Interlocked.Increment(ref hookFireCount[baseIndex]));
        hooks.AfterCommit(() => Interlocked.Increment(ref hookFireCount[baseIndex + 1]));
        hooks.AfterCommit(() => Interlocked.Increment(ref hookFireCount[baseIndex + 2]));
    }
}
