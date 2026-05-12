using System.Transactions;
using Gsag.Transactional.Core.Attributes;
using Gsag.Transactional.Core.Proxy;
using Xunit;

namespace Gsag.Transactional.Tests.Unit;

public interface IPropagationService
{
    [Transactional(Propagation = TransactionScopeOption.RequiresNew)]
    string RequiresNew();

    [Transactional(Propagation = TransactionScopeOption.Suppress)]
    string Suppress();
}

public class PropagationService : IPropagationService
{
    public string RequiresNew() => "ok";
    public string Suppress() => Transaction.Current is null ? "suppressed" : "not-suppressed";
}

public class PropagationTests
{
    private readonly IPropagationService _proxy;

    public PropagationTests() =>
        _proxy = TransactionProxyFactory.Create<IPropagationService>(new PropagationService());

    [Fact]
    public void RequiresNew_ExecutesWithIndependentTransaction()
        => Assert.Equal("ok", _proxy.RequiresNew());

    [Fact]
    public void Suppress_WhenCalledInsideAmbientScope_TransactionCurrentIsNull()
    {
        using var outer = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);
        var result = _proxy.Suppress();
        Assert.Equal("suppressed", result);
        outer.Complete();
    }

    [Fact]
    public void RequiresNew_InsideAmbientScope_SuspendsAndRestoresOuterTransaction()
    {
        using var outer = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);
        var outerTx = Transaction.Current;

        _proxy.RequiresNew();

        Assert.Same(outerTx, Transaction.Current);
    }
}
