using System.Transactions;
using Gsag.Transactional.Core.Attributes;
using Gsag.Transactional.Core.Hooks;
using Xunit;

namespace Gsag.Transactional.Tests.Core.Unit.Hooks;

public class TransactionHooksInternalTests
{
    [Fact]
    public void BeginScope_WithInvalidPropagation_ThrowsArgumentOutOfRangeException()
    {
        var attr = new TransactionalAttribute { Propagation = (TransactionScopeOption)999 };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => TransactionHooks.BeginScope(attr));

        Assert.Equal("attr", ex.ParamName);
        Assert.Contains("Unsupported TransactionScopeOption value", ex.Message);
        Assert.Contains("999", ex.Message);
    }

    [Fact]
    public void BeginScope_WhenJoiningAmbientRequired_ReturnsJoiningCollectionAndPreservesOuterScope()
    {
        var attr = new TransactionalAttribute { Propagation = TransactionScopeOption.Required };
        var outer = TransactionHooks.BeginScope(attr);

        try
        {
            using var ambient = new TransactionScope(TransactionScopeOption.Required, TransactionScopeAsyncFlowOption.Enabled);

            var joining = TransactionHooks.BeginScope(attr);

            try
            {
                Assert.Equal(HookCollectionRole.Joining, joining.Role);
                Assert.Same(outer, joining.Previous);

                TransactionHooks.ClearScope(joining);

                var secondJoining = TransactionHooks.BeginScope(attr);
                try
                {
                    Assert.Equal(HookCollectionRole.Joining, secondJoining.Role);
                    Assert.Same(outer, secondJoining.Previous);
                }
                finally
                {
                    TransactionHooks.ClearScope(secondJoining);
                }
            }
            finally
            {
                ambient.Complete();
            }
        }
        finally
        {
            TransactionHooks.ClearScope(outer);
        }
    }

    [Fact]
    public void BeginScope_WhenSuppressing_ReturnsThrowawayCollectionAndRestoresPreviousScope()
    {
        var attr = new TransactionalAttribute { Propagation = TransactionScopeOption.Required };
        var outer = TransactionHooks.BeginScope(attr);
        var suppressAttr = new TransactionalAttribute { Propagation = TransactionScopeOption.Suppress };

        try
        {
            var suppress = TransactionHooks.BeginScope(suppressAttr);

            try
            {
                Assert.Equal(HookCollectionRole.SuppressThrowaway, suppress.Role);
                Assert.Same(outer, suppress.Previous);

                TransactionHooks.ClearScope(suppress);

                var restored = TransactionHooks.BeginScope(attr);
                try
                {
                    Assert.Equal(HookCollectionRole.Owning, restored.Role);
                    Assert.Same(outer, restored.Previous);
                }
                finally
                {
                    TransactionHooks.ClearScope(restored);
                }
            }
            finally
            {
                TransactionHooks.ClearScope(suppress);
            }
        }
        finally
        {
            TransactionHooks.ClearScope(outer);
        }
    }

    [Fact]
    public async Task TriggerAsync_WhenMultipleHooksThrow_CollectsAllExceptionsAndMentionsTheEventName()
    {
        var hooks = new HookCollection();
        hooks.AddAsync(HookEvent.AfterCommit, () => throw new InvalidOperationException("first"));
        hooks.AddAsync(HookEvent.AfterCommit, () => throw new InvalidOperationException("second"));

        var ex = await Assert.ThrowsAsync<AggregateException>(() => TransactionHooks.TriggerAsync(hooks, HookEvent.AfterCommit));

        Assert.Equal(2, ex.InnerExceptions.Count);
        Assert.Contains("after-commit", ex.Message);
    }

    [Fact]
    public async Task TriggerAsync_WhenSuppressExceptionsIsTrue_DoesNotThrow()
    {
        var hooks = new HookCollection();
        hooks.AddAsync(HookEvent.AfterCommit, () => throw new InvalidOperationException("boom"));

        await TransactionHooks.TriggerAsync(hooks, HookEvent.AfterCommit, suppressExceptions: true);
    }

    [Fact]
    public void RunBeforeCommitSyncHooks_WhenAsyncHooksAreRegistered_ThrowsNotSupportedException()
    {
        var hooks = new HookCollection();
        hooks.AddAsync(HookEvent.BeforeCommit, () => Task.CompletedTask);

        var ex = Assert.Throws<NotSupportedException>(() => TransactionHooks.RunBeforeCommitSyncHooks(hooks));

        Assert.Contains("Async hooks cannot be awaited", ex.Message);
    }

    [Fact]
    public void RunBeforeRollbackSyncHooks_WhenAsyncHooksAreRegistered_ThrowsNotSupportedException()
    {
        var hooks = new HookCollection();
        hooks.AddAsync(HookEvent.BeforeRollback, () => Task.CompletedTask);

        var ex = Assert.Throws<NotSupportedException>(() => TransactionHooks.RunBeforeRollbackSyncHooks(hooks));

        Assert.Contains("Async hooks cannot be awaited", ex.Message);
    }

    [Fact]
    public void RunBeforeCommitHooksAsync_WhenNoHooksAreRegistered_ReturnsCompletedTask()
    {
        var hooks = new HookCollection();

        Assert.Same(Task.CompletedTask, TransactionHooks.RunBeforeCommitHooksAsync(hooks));
    }

    [Fact]
    public void RunBeforeRollbackHooksAsync_WhenNoHooksAreRegistered_ReturnsCompletedTask()
    {
        var hooks = new HookCollection();

        Assert.Same(Task.CompletedTask, TransactionHooks.RunBeforeRollbackHooksAsync(hooks));
    }

    [Fact]
    public void EnsureNoAsyncHooks_WhenOnlySyncHooksAreRegistered_DoesNotThrow()
    {
        var hooks = new HookCollection();
        hooks.AddSync(HookEvent.AfterCommit, () => { });

        TransactionHooks.EnsureNoAsyncHooks(hooks, HookEvent.AfterCommit);
    }

    [Fact]
    public void RunSyncHooks_WhenCommitted_FiresAfterCommitAndAfterCompletionOnly()
    {
        var hooks = new HookCollection();
        var fired = new List<string>();

        hooks.AddSync(HookEvent.AfterCommit, () => fired.Add("after-commit"));
        hooks.AddSync(HookEvent.AfterRollback, () => fired.Add("after-rollback"));
        hooks.AddSync(HookEvent.AfterCompletion, () => fired.Add("after-completion"));

        TransactionHooks.RunSyncHooks(hooks, TransactionOutcome.Committed);

        Assert.Equal(["after-commit", "after-completion"], fired);
    }

    [Fact]
    public void RunSyncHooks_WhenRolledBack_FiresAfterRollbackAndAfterCompletionOnly()
    {
        var hooks = new HookCollection();
        var fired = new List<string>();

        hooks.AddSync(HookEvent.AfterCommit, () => fired.Add("after-commit"));
        hooks.AddSync(HookEvent.AfterRollback, () => fired.Add("after-rollback"));
        hooks.AddSync(HookEvent.AfterCompletion, () => fired.Add("after-completion"));

        TransactionHooks.RunSyncHooks(hooks, TransactionOutcome.RolledBack);

        Assert.Equal(["after-rollback", "after-completion"], fired);
    }
}
