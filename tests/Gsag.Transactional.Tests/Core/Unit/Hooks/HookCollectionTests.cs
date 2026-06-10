using Gsag.Transactional.Core.Hooks;
using Xunit;

namespace Gsag.Transactional.Tests.Core.Unit.Hooks;

public class HookCollectionTests
{
    [Fact]
    public void HasHooksFor_WhenNothingRegistered_ReturnsFalse()
    {
        var collection = new HookCollection();

        Assert.False(collection.HasHooksFor(HookEvent.AfterCommit));
    }

    [Fact]
    public void HasHooksFor_AfterAddSync_ReturnsTrue()
    {
        var collection = new HookCollection();
        collection.AddSync(HookEvent.AfterCommit, () => { });

        Assert.True(collection.HasHooksFor(HookEvent.AfterCommit));
    }

    [Fact]
    public void HasHooksFor_AfterAddAsync_ReturnsTrue()
    {
        var collection = new HookCollection();
        collection.AddAsync(HookEvent.AfterCommit, () => Task.CompletedTask);

        Assert.True(collection.HasHooksFor(HookEvent.AfterCommit));
    }

    [Fact]
    public void HasHooksFor_ForEventA_ReturnsFalse_WhenOnlyEventBIsRegistered()
    {
        var collection = new HookCollection();
        collection.AddSync(HookEvent.BeforeCommit, () => { });

        Assert.False(collection.HasHooksFor(HookEvent.AfterCommit));
    }

    [Fact]
    public void HasHooksFor_ForEventA_ReturnsFalse_WhenOnlyAsyncEventBIsRegistered()
    {
        var collection = new HookCollection();
        collection.AddAsync(HookEvent.BeforeRollback, () => Task.CompletedTask);

        Assert.False(collection.HasHooksFor(HookEvent.AfterRollback));
    }

    [Fact]
    public void SyncFor_WhenNothingRegistered_ReturnsEmptyList()
    {
        var collection = new HookCollection();

        var result = collection.SyncFor(HookEvent.AfterCommit);

        Assert.Empty(result);
    }

    [Fact]
    public void AsyncFor_WhenNothingRegistered_ReturnsEmptyList()
    {
        var collection = new HookCollection();

        var result = collection.AsyncFor(HookEvent.AfterCommit);

        Assert.Empty(result);
    }

    [Fact]
    public void SyncFor_ReturnsRegisteredAction()
    {
        var collection = new HookCollection();
        var fired = false;
        collection.AddSync(HookEvent.AfterCommit, () => { fired = true; });

        var hooks = collection.SyncFor(HookEvent.AfterCommit);
        Assert.Single(hooks);
        hooks[0]();

        Assert.True(fired);
    }

    [Fact]
    public void AsyncFor_ReturnsRegisteredFunc()
    {
        var collection = new HookCollection();
        var fired = false;
        collection.AddAsync(HookEvent.AfterCommit, () =>
        {
            fired = true;
            return Task.CompletedTask;
        });

        var hooks = collection.AsyncFor(HookEvent.AfterCommit);
        Assert.Single(hooks);
        hooks[0]();

        Assert.True(fired);
    }

    [Fact]
    public void AddSync_MultipleTimes_SameEvent_AllActionsInList()
    {
        var collection = new HookCollection();
        var log = new List<int>();
        collection.AddSync(HookEvent.AfterCommit, () => log.Add(1));
        collection.AddSync(HookEvent.AfterCommit, () => log.Add(2));
        collection.AddSync(HookEvent.AfterCommit, () => log.Add(3));

        var hooks = collection.SyncFor(HookEvent.AfterCommit);
        Assert.Equal(3, hooks.Count);
        foreach (var h in hooks)
        {
            h();
        }

        Assert.Equal([1, 2, 3], log);
    }

    [Fact]
    public void AddAsync_MultipleTimes_SameEvent_AllFuncsInList()
    {
        var collection = new HookCollection();
        var log = new List<int>();
        collection.AddAsync(HookEvent.AfterRollback, () => { log.Add(1); return Task.CompletedTask; });
        collection.AddAsync(HookEvent.AfterRollback, () => { log.Add(2); return Task.CompletedTask; });

        var hooks = collection.AsyncFor(HookEvent.AfterRollback);
        Assert.Equal(2, hooks.Count);
        foreach (var h in hooks)
        {
            h();
        }

        Assert.Equal([1, 2], log);
    }

    [Fact]
    public void SyncFor_DifferentEvents_DoNotCrossContaminate()
    {
        var collection = new HookCollection();
        collection.AddSync(HookEvent.BeforeCommit, () => { });

        Assert.Empty(collection.SyncFor(HookEvent.AfterCommit));
        Assert.Single(collection.SyncFor(HookEvent.BeforeCommit));
    }

    [Fact]
    public void AsyncFor_DifferentEvents_DoNotCrossContaminate()
    {
        var collection = new HookCollection();
        collection.AddAsync(HookEvent.BeforeRollback, () => Task.CompletedTask);

        Assert.Empty(collection.AsyncFor(HookEvent.AfterRollback));
        Assert.Single(collection.AsyncFor(HookEvent.BeforeRollback));
    }

    [Fact]
    public void HasHooksFor_ReturnsFalse_ForAllEventsOnFreshCollection()
    {
        var collection = new HookCollection();

        foreach (HookEvent evt in Enum.GetValues<HookEvent>())
        {
            Assert.False(collection.HasHooksFor(evt));
        }
    }

    [Fact]
    public void HasHooksFor_ReturnsTrue_OnlyForRegisteredEvent_WhenMixedEventsAdded()
    {
        var collection = new HookCollection();
        collection.AddSync(HookEvent.AfterCompletion, () => { });

        Assert.True(collection.HasHooksFor(HookEvent.AfterCompletion));
        Assert.False(collection.HasHooksFor(HookEvent.BeforeCommit));
        Assert.False(collection.HasHooksFor(HookEvent.AfterCommit));
        Assert.False(collection.HasHooksFor(HookEvent.BeforeRollback));
        Assert.False(collection.HasHooksFor(HookEvent.AfterRollback));
    }
}
