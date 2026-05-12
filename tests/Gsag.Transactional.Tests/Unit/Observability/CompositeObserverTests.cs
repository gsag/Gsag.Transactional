using Gsag.Transactional.Core.Attributes;
using Gsag.Transactional.Core.Observability;
using Gsag.Transactional.Core.Proxy;
using Xunit;
using Gsag.Transactional.Tests.Unit;

namespace Gsag.Transactional.Tests.Unit.Observability;

// ---------------------------------------------------------------------------
// Service doubles
// ---------------------------------------------------------------------------

public interface ICompositeObserverService
{
    [Transactional]
    void Commit();

    [Transactional]
    void Throw();
}

public class CompositeObserverService : ICompositeObserverService
{
    public void Commit() { }
    public void Throw() => throw new InvalidOperationException("rollback-me");
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

public class CompositeObserverTests
{
    // -------------------------------------------------------------------------
    // OnComplete
    // -------------------------------------------------------------------------

    [Fact]
    public void OnComplete_WhenCommits_ReportsCommittedTrue()
    {
        var observer = new RecordingObserver();
        var proxy = TransactionProxyFactory.Create<ICompositeObserverService>(
            new CompositeObserverService(), observer);

        proxy.Commit();

        Assert.Contains("COMPLETE:Commit:True", observer.Calls);
    }

    [Fact]
    public void OnComplete_WhenRollsBack_ReportsCommittedFalse()
    {
        var observer = new RecordingObserver();
        var proxy = TransactionProxyFactory.Create<ICompositeObserverService>(
            new CompositeObserverService(), observer);

        Assert.Throws<InvalidOperationException>(() => proxy.Throw());

        Assert.Contains("COMPLETE:Throw:False", observer.Calls);
    }

    [Fact]
    public void OnComplete_FiresAfterCommit()
    {
        var observer = new RecordingObserver();
        var proxy = TransactionProxyFactory.Create<ICompositeObserverService>(
            new CompositeObserverService(), observer);

        proxy.Commit();

        var commitIndex  = observer.Calls.IndexOf("COMMIT:Commit");
        var completeIndex = observer.Calls.IndexOf("COMPLETE:Commit:True");
        Assert.True(commitIndex < completeIndex, "OnComplete must fire after OnCommit");
    }

    [Fact]
    public void OnComplete_FiresAfterRollback()
    {
        var observer = new RecordingObserver();
        var proxy = TransactionProxyFactory.Create<ICompositeObserverService>(
            new CompositeObserverService(), observer);

        Assert.Throws<InvalidOperationException>(() => proxy.Throw());

        var rollbackIndex  = observer.Calls.IndexOf("ROLLBACK:Throw");
        var completeIndex  = observer.Calls.IndexOf("COMPLETE:Throw:False");
        Assert.True(rollbackIndex < completeIndex, "OnComplete must fire after OnRollback");
    }

    // -------------------------------------------------------------------------
    // Composite — multiple observers
    // -------------------------------------------------------------------------

    [Fact]
    public void Composite_TwoObservers_BothReceiveAllEvents()
    {
        var obs1 = new RecordingObserver();
        var obs2 = new RecordingObserver();
        var composite = new CompositeTransactionObserver([obs1, obs2]);
        var proxy = TransactionProxyFactory.Create<ICompositeObserverService>(
            new CompositeObserverService(), composite);

        proxy.Commit();

        foreach (var obs in new[] { obs1, obs2 })
        {
            Assert.Contains("BEGIN:Commit",          obs.Calls);
            Assert.Contains("COMMIT:Commit",         obs.Calls);
            Assert.Contains("COMPLETE:Commit:True",  obs.Calls);
        }
    }

    [Fact]
    public void Composite_TwoObservers_EventsDispatchedInRegistrationOrder()
    {
        var order = new List<string>();
        var obs1 = new OrderTrackingObserver("first",  order);
        var obs2 = new OrderTrackingObserver("second", order);
        var composite = new CompositeTransactionObserver([obs1, obs2]);
        var proxy = TransactionProxyFactory.Create<ICompositeObserverService>(
            new CompositeObserverService(), composite);

        proxy.Commit();

        var commitFirst  = order.IndexOf("first:COMMIT");
        var commitSecond = order.IndexOf("second:COMMIT");
        Assert.True(commitFirst < commitSecond, "first observer must fire before second");
    }

    [Fact]
    public void Composite_WhenRollsBack_BothObserversReceiveRollbackAndComplete()
    {
        var obs1 = new RecordingObserver();
        var obs2 = new RecordingObserver();
        var composite = new CompositeTransactionObserver([obs1, obs2]);
        var proxy = TransactionProxyFactory.Create<ICompositeObserverService>(
            new CompositeObserverService(), composite);

        Assert.Throws<InvalidOperationException>(() => proxy.Throw());

        foreach (var obs in new[] { obs1, obs2 })
        {
            Assert.Contains("ROLLBACK:Throw",        obs.Calls);
            Assert.Contains("COMPLETE:Throw:False",  obs.Calls);
            Assert.DoesNotContain("COMMIT:Throw",    obs.Calls);
        }
    }

    // -------------------------------------------------------------------------
    // Composite — fail-fast on observer exception
    // -------------------------------------------------------------------------

    [Fact]
    public void Composite_WhenFirstObserverThrowsOnCommit_ExceptionPropagates()
    {
        var throwingObs = new ThrowingOnCommitObserver();
        var second      = new RecordingObserver();
        var composite   = new CompositeTransactionObserver([throwingObs, second]);
        var proxy       = TransactionProxyFactory.Create<ICompositeObserverService>(
            new CompositeObserverService(), composite);

        var ex = Assert.Throws<InvalidOperationException>(() => proxy.Commit());

        Assert.Equal("observer-commit-fail", ex.Message);
        // Second observer was not called because first threw (fail-fast).
        Assert.DoesNotContain("COMMIT:Commit", second.Calls);
    }
}

// ---------------------------------------------------------------------------
// Additional helper — tracks event dispatch order across observer instances
// ---------------------------------------------------------------------------

file class OrderTrackingObserver : ITransactionObserver
{
    private readonly string _name;
    private readonly List<string> _order;

    public OrderTrackingObserver(string name, List<string> order)
    {
        _name  = name;
        _order = order;
    }

    public void OnBegin(TransactionInfo info) =>
        _order.Add($"{_name}:BEGIN");

    public void OnCommit(TransactionInfo info, TimeSpan elapsed) =>
        _order.Add($"{_name}:COMMIT");

    public void OnRollback(TransactionInfo info, Exception exception, TimeSpan elapsed) =>
        _order.Add($"{_name}:ROLLBACK");

    public void OnComplete(TransactionInfo info, bool committed, TimeSpan elapsed) =>
        _order.Add($"{_name}:COMPLETE");
}
