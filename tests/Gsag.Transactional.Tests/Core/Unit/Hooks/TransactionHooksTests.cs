using Gsag.Transactional.Core.Hooks;
using Xunit;

namespace Gsag.Transactional.Tests.Core.Unit.Hooks;

/// <summary>
/// Verifies that hook registrations outside an active [Transactional] scope are silently
/// dropped (null-conditional on _current.Value). Each method has two overloads — one for
/// sync <see cref="Action"/> and one for async <see cref="Func{Task}"/>. Both must be no-ops
/// when called outside a scope so that application code calling hooks unconditionally does
/// not throw <see cref="NullReferenceException"/>.
/// </summary>
public class TransactionHooksTests
{
    [Fact]
    public void AfterRollback_OutsideAnyScope_SyncAndAsync_AreNoOps()
    {
        var hooks = new TransactionHooks();

        hooks.AfterRollback((Action)(() => throw new Exception("should not fire")));
        hooks.AfterRollback(async () => { await Task.CompletedTask; throw new Exception("should not fire"); });
    }

    [Fact]
    public void AfterCompletion_OutsideAnyScope_SyncAndAsync_AreNoOps()
    {
        var hooks = new TransactionHooks();

        hooks.AfterCompletion((Action)(() => throw new Exception("should not fire")));
        hooks.AfterCompletion(async () => { await Task.CompletedTask; throw new Exception("should not fire"); });
    }

    [Fact]
    public void BeforeCommit_OutsideAnyScope_SyncAndAsync_AreNoOps()
    {
        var hooks = new TransactionHooks();

        hooks.BeforeCommit((Action)(() => throw new Exception("should not fire")));
        hooks.BeforeCommit(async () => { await Task.CompletedTask; throw new Exception("should not fire"); });
    }

    [Fact]
    public void BeforeRollback_OutsideAnyScope_SyncAndAsync_AreNoOps()
    {
        var hooks = new TransactionHooks();

        hooks.BeforeRollback((Action)(() => throw new Exception("should not fire")));
        hooks.BeforeRollback(async () => { await Task.CompletedTask; throw new Exception("should not fire"); });
    }
}
