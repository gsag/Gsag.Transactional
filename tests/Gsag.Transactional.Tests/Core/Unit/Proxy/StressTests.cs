using Gsag.Transactional.Core.Attributes;
using Gsag.Transactional.Core.Proxy;
using Xunit;

namespace Gsag.Transactional.Tests.Core.Unit.Proxy;

public interface IStressService
{
    [Transactional]
    Task<int> AddAsync(int value);

    [Transactional]
    Task<int> GetAsync();
}

public class StressService : IStressService
{
    private int _total;

    public Task<int> AddAsync(int value) =>
        Task.FromResult(Interlocked.Add(ref _total, value));

    public Task<int> GetAsync() =>
        Task.FromResult(Volatile.Read(ref _total));
}

public class StressTests
{
    [Fact]
    public async Task Stress_WhenManyParallelInvocations_AllCommitWithoutError()
    {
        const int count = 200;
        var svc = new StressService();
        var proxy = TransactionProxyFactory.Create<IStressService>(svc);

        var tasks = Enumerable.Range(0, count).Select(_ => proxy.AddAsync(1)).ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(count, results.Distinct().Count());
        Assert.Equal(count, await proxy.GetAsync());
    }

    [Fact]
    public async Task Stress_WhenRapidSequentialInvocations_AllCommitWithoutError()
    {
        const int count = 500;
        var svc = new StressService();
        var proxy = TransactionProxyFactory.Create<IStressService>(svc);

        for (int i = 0; i < count; i++)
        {
            await proxy.AddAsync(1);
        }

        Assert.Equal(count, await proxy.GetAsync());
    }
}
