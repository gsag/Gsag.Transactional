using System.Transactions;
using Gsag.Transactional.Core.Attributes;
using Gsag.Transactional.Core.Hooks;
using Gsag.Transactional.Core.Proxy;
using Xunit;

namespace Gsag.Transactional.Tests.Integration.Hooks;

// ---------------------------------------------------------------------------
// RequiresNew nesting doubles
// ---------------------------------------------------------------------------

public interface IHookInnerService
{
    Task RunAsync();
}

public class HookInnerService : IHookInnerService
{
    private readonly ITransactionHooks _hooks;
    public List<string> Fired { get; } = [];
    public HookInnerService(ITransactionHooks hooks) => _hooks = hooks;

    [Transactional(Propagation = TransactionScopeOption.RequiresNew)]
    public async Task RunAsync()
    {
        await Task.CompletedTask;
        _hooks.AfterCommit(() => Fired.Add("inner-hook"));
    }
}

public interface IHookOuterService
{
    Task RunAsync();
}

public class HookOuterService : IHookOuterService
{
    private readonly ITransactionHooks _hooks;
    private readonly IHookInnerService _inner;
    public List<string> Fired { get; } = [];

    public HookOuterService(ITransactionHooks hooks, IHookInnerService inner)
    {
        _hooks = hooks;
        _inner = inner;
    }

    [Transactional]
    public async Task RunAsync()
    {
        _hooks.AfterCommit(() => Fired.Add("outer-hook-before"));
        await _inner.RunAsync();
        // Without the BeginScope restore fix this hook would be silently dropped:
        // the inner RequiresNew scope clobbers _current.Value and ClearScope nulls it.
        _hooks.AfterCommit(() => Fired.Add("outer-hook-after"));
    }
}

// ---------------------------------------------------------------------------
// Suppress nesting doubles
// ---------------------------------------------------------------------------

public interface ISuppressService
{
    Task RunSuppressedAsync();
}

public class SuppressService : ISuppressService
{
    private readonly ITransactionHooks _hooks;
    public List<string> Fired { get; } = [];
    public SuppressService(ITransactionHooks hooks) => _hooks = hooks;

    [Transactional(Propagation = TransactionScopeOption.Suppress)]
    public async Task RunSuppressedAsync()
    {
        await Task.CompletedTask;
        // _current is null inside Suppress — this is a no-op by design.
        _hooks.AfterCommit(() => Fired.Add("suppress-hook"));
    }
}

public interface ISuppressOuterService
{
    Task RunAsync();
}

public class SuppressOuterService : ISuppressOuterService
{
    private readonly ITransactionHooks _hooks;
    private readonly ISuppressService _inner;
    public List<string> Fired { get; } = [];

    public SuppressOuterService(ITransactionHooks hooks, ISuppressService inner)
    {
        _hooks = hooks;
        _inner = inner;
    }

    [Transactional]
    public async Task RunAsync()
    {
        _hooks.AfterCommit(() => Fired.Add("outer-hook-before"));
        await _inner.RunSuppressedAsync();
        // Without the Suppress restore fix, _current is left null here and this hook is lost.
        _hooks.AfterCommit(() => Fired.Add("outer-hook-after"));
    }
}

// ---------------------------------------------------------------------------
// Three-level nesting: Required → Suppress → RequiresNew
// ---------------------------------------------------------------------------

public interface IRequiresNewInSuppressService
{
    Task RunAsync();
}

public class RequiresNewInSuppressService : IRequiresNewInSuppressService
{
    private readonly ITransactionHooks _hooks;
    public List<string> Fired { get; } = [];
    public RequiresNewInSuppressService(ITransactionHooks hooks) => _hooks = hooks;

    [Transactional(Propagation = TransactionScopeOption.RequiresNew)]
    public async Task RunAsync()
    {
        await Task.CompletedTask;
        _hooks.AfterCommit(() => Fired.Add("requiresnew-hook"));
    }
}

public interface ISuppressWithRequiresNewService
{
    Task RunAsync();
}

public class SuppressWithRequiresNewService : ISuppressWithRequiresNewService
{
    private readonly IRequiresNewInSuppressService _inner;
    public SuppressWithRequiresNewService(IRequiresNewInSuppressService inner) => _inner = inner;

    [Transactional(Propagation = TransactionScopeOption.Suppress)]
    public async Task RunAsync()
    {
        await _inner.RunAsync();
    }
}

public interface IOuterWithSuppressAndRequiresNewService
{
    Task RunAsync();
}

public class OuterWithSuppressAndRequiresNewService : IOuterWithSuppressAndRequiresNewService
{
    private readonly ITransactionHooks _hooks;
    private readonly ISuppressWithRequiresNewService _mid;
    public List<string> Fired { get; } = [];

    public OuterWithSuppressAndRequiresNewService(ITransactionHooks hooks, ISuppressWithRequiresNewService mid)
    {
        _hooks = hooks;
        _mid = mid;
    }

    [Transactional]
    public async Task RunAsync()
    {
        _hooks.AfterCommit(() => Fired.Add("outer-hook-before"));
        await _mid.RunAsync();
        _hooks.AfterCommit(() => Fired.Add("outer-hook-after"));
    }
}

// ---------------------------------------------------------------------------
// Required inside Suppress — no ambient inside Suppress, so Required opens its own scope
// ---------------------------------------------------------------------------

public interface IRequiredInSuppressInner
{
    Task RunAsync();
}

public class RequiredInSuppressInner : IRequiredInSuppressInner
{
    private readonly ITransactionHooks _hooks;
    public List<string> Fired { get; } = [];
    public RequiredInSuppressInner(ITransactionHooks hooks) => _hooks = hooks;

    [Transactional]
    public async Task RunAsync()
    {
        await Task.CompletedTask;
        _hooks.AfterCommit(() => Fired.Add("inner-hook"));
    }
}

public interface IRequiredInSuppressOuter
{
    Task RunAsync();
}

public class RequiredInSuppressOuter : IRequiredInSuppressOuter
{
    private readonly IRequiredInSuppressInner _inner;
    public RequiredInSuppressOuter(IRequiredInSuppressInner inner) => _inner = inner;

    [Transactional(Propagation = TransactionScopeOption.Suppress)]
    public async Task RunAsync()
    {
        await _inner.RunAsync();
    }
}

// ---------------------------------------------------------------------------
// Required joining an ambient Required scope
// ---------------------------------------------------------------------------

public interface IRequiredJoinInnerService
{
    Task RunAsync();
}

public class RequiredJoinInnerService : IRequiredJoinInnerService
{
    private readonly ITransactionHooks _hooks;
    public List<string> Fired { get; } = [];
    public RequiredJoinInnerService(ITransactionHooks hooks) => _hooks = hooks;

    [Transactional]
    public async Task RunAsync()
    {
        await Task.CompletedTask;
        _hooks.AfterCommit(() => Fired.Add("inner-hook"));
    }
}

public interface IRequiredJoinOuterService
{
    Task RunAsync();
}

public class RequiredJoinOuterService : IRequiredJoinOuterService
{
    private readonly ITransactionHooks _hooks;
    private readonly IRequiredJoinInnerService _inner;
    public List<string> Fired { get; } = [];

    public RequiredJoinOuterService(ITransactionHooks hooks, IRequiredJoinInnerService inner)
    {
        _hooks = hooks;
        _inner = inner;
    }

    [Transactional]
    public async Task RunAsync()
    {
        _hooks.AfterCommit(() => Fired.Add("outer-hook"));
        await _inner.RunAsync();
    }
}

// ---------------------------------------------------------------------------
// Required joining ambient — inner throws (NoRollbackFor), outer catches and commits
// ---------------------------------------------------------------------------

public interface IRequiredJoinThrowingInnerService
{
    Task RunAsync();
}

public class RequiredJoinThrowingInnerService : IRequiredJoinThrowingInnerService
{
    private readonly ITransactionHooks _hooks;
    public List<string> Fired { get; } = [];
    public RequiredJoinThrowingInnerService(ITransactionHooks hooks) => _hooks = hooks;

    [Transactional(NoRollbackFor = [typeof(InvalidOperationException)])]
    public async Task RunAsync()
    {
        await Task.CompletedTask;
        _hooks.AfterCommit(() => Fired.Add("inner-hook"));
        throw new InvalidOperationException("inner-error");
    }
}

public interface IRequiredJoinCatchingOuterService
{
    Task RunAsync();
}

public class RequiredJoinCatchingOuterService : IRequiredJoinCatchingOuterService
{
    private readonly ITransactionHooks _hooks;
    private readonly IRequiredJoinThrowingInnerService _inner;
    public List<string> Fired { get; } = [];

    public RequiredJoinCatchingOuterService(ITransactionHooks hooks, IRequiredJoinThrowingInnerService inner)
    {
        _hooks = hooks;
        _inner = inner;
    }

    [Transactional]
    public async Task RunAsync()
    {
        _hooks.AfterCommit(() => Fired.Add("outer-hook-before"));
        try
        {
            await _inner.RunAsync();
        }
        catch (InvalidOperationException)
        {
            // swallow — outer continues and commits
        }
        _hooks.AfterCommit(() => Fired.Add("outer-hook-after"));
    }
}

// ---------------------------------------------------------------------------

public class HookScopeTests
{
    /// <summary>
    /// Regression: RequiresNew overwrote _current.Value without restoring it.
    /// Hooks registered on the outer scope after the inner RequiresNew returned
    /// were silently dropped because _current.Value was null.
    /// </summary>
    [Fact]
    public async Task RequiresNew_BothScopesRegisterHooks_BothHooksFire()
    {
        var hooks      = new TransactionHooks();
        var innerSvc   = new HookInnerService(hooks);
        var innerProxy = TransactionProxyFactory.Create<IHookInnerService>(innerSvc, observer: null);
        var outerSvc   = new HookOuterService(hooks, innerProxy);
        var outerProxy = TransactionProxyFactory.Create<IHookOuterService>(outerSvc, observer: null);

        await outerProxy.RunAsync();

        // Inner hook fires when the RequiresNew scope commits (inside outerProxy.RunAsync).
        Assert.Equal(["inner-hook"], innerSvc.Fired);
        // Both outer hooks fire when the outer Required scope commits.
        // outer-hook-after would be lost without the BeginScope restore fix.
        Assert.Equal(["outer-hook-before", "outer-hook-after"], outerSvc.Fired);
    }

    /// <summary>
    /// Regression: Suppress set _current.Value = null without restoring Previous.
    /// Hooks registered on the outer scope after the Suppress call returned were dropped.
    /// </summary>
    [Fact]
    public async Task Suppress_HooksInOuterScopeAroundSuppressedCall_AllOuterHooksFire()
    {
        var hooks      = new TransactionHooks();
        var innerSvc   = new SuppressService(hooks);
        var innerProxy = TransactionProxyFactory.Create<ISuppressService>(innerSvc, observer: null);
        var outerSvc   = new SuppressOuterService(hooks, innerProxy);
        var outerProxy = TransactionProxyFactory.Create<ISuppressOuterService>(outerSvc, observer: null);

        await outerProxy.RunAsync();

        // Hook registered inside the Suppress scope is a no-op — _current is null there.
        Assert.Empty(innerSvc.Fired);
        // Both outer hooks fire; outer-hook-after would be lost without the Suppress restore fix.
        Assert.Equal(["outer-hook-before", "outer-hook-after"], outerSvc.Fired);
    }

    /// <summary>
    /// When a Required service is called from inside a Suppress scope, Transaction.Current is null,
    /// so the Required service opens its own independent scope (same as RequiresNew in this context).
    /// Its hooks fire when its own scope commits, not when any outer scope commits.
    /// </summary>
    [Fact]
    public async Task Required_InsideSuppressScope_OpensOwnScopeAndHooksFire()
    {
        var hooks      = new TransactionHooks();
        var innerSvc   = new RequiredInSuppressInner(hooks);
        var innerProxy = TransactionProxyFactory.Create<IRequiredInSuppressInner>(innerSvc, observer: null);
        var outerSvc   = new RequiredInSuppressOuter(innerProxy);
        var outerProxy = TransactionProxyFactory.Create<IRequiredInSuppressOuter>(outerSvc, observer: null);

        await outerProxy.RunAsync();

        // Inner Required opens its own scope (no ambient inside Suppress) and fires its hook.
        Assert.Equal(["inner-hook"], innerSvc.Fired);
    }

    /// <summary>
    /// When an inner Required scope joins an existing ambient Required scope, hooks registered
    /// inside the inner call flow into the outer HookCollection and fire once when the outer
    /// scope commits — not when the inner scope's wrapper runs.
    /// </summary>
    [Fact]
    public async Task Required_JoiningAmbientScope_HooksAccumulateInOuterCollectionAndFireOnce()
    {
        var hooks      = new TransactionHooks();
        var innerSvc   = new RequiredJoinInnerService(hooks);
        var innerProxy = TransactionProxyFactory.Create<IRequiredJoinInnerService>(innerSvc, observer: null);
        var outerSvc   = new RequiredJoinOuterService(hooks, innerProxy);
        var outerProxy = TransactionProxyFactory.Create<IRequiredJoinOuterService>(outerSvc, observer: null);

        await outerProxy.RunAsync();

        // Inner hook flows into the outer collection and fires when the outer scope commits.
        Assert.Equal(["inner-hook"], innerSvc.Fired);
        Assert.Equal(["outer-hook"], outerSvc.Fired);
    }

    /// <summary>
    /// When a Required inner scope joins the outer ambient scope and throws (with NoRollbackFor
    /// allowing the inner scope to commit), the outer scope can still commit. Hooks registered
    /// during the inner body flow into the outer collection and fire on the outer commit.
    /// </summary>
    [Fact]
    public async Task Required_JoiningScope_WhenInnerThrowsAndOuterCommits_HookOutcomeDrivenByOuterScope()
    {
        var hooks      = new TransactionHooks();
        var innerSvc   = new RequiredJoinThrowingInnerService(hooks);
        var innerProxy = TransactionProxyFactory.Create<IRequiredJoinThrowingInnerService>(innerSvc, observer: null);
        var outerSvc   = new RequiredJoinCatchingOuterService(hooks, innerProxy);
        var outerProxy = TransactionProxyFactory.Create<IRequiredJoinCatchingOuterService>(outerSvc, observer: null);

        await outerProxy.RunAsync();

        // Inner hook flows into outer collection; all hooks fire on outer commit.
        Assert.Equal(["inner-hook"], innerSvc.Fired);
        Assert.Equal(["outer-hook-before", "outer-hook-after"], outerSvc.Fired);
    }

    /// <summary>
    /// Three-level nesting: Required (outer) → Suppress (mid) → RequiresNew (inner).
    /// The outer hooks registered around the Suppress+RequiresNew chain must still fire.
    /// </summary>
    [Fact]
    public async Task Suppress_ContainingRequiresNew_OuterHooksStillFire()
    {
        var hooks      = new TransactionHooks();
        var innerSvc   = new RequiresNewInSuppressService(hooks);
        var innerProxy = TransactionProxyFactory.Create<IRequiresNewInSuppressService>(innerSvc, observer: null);
        var midSvc     = new SuppressWithRequiresNewService(innerProxy);
        var midProxy   = TransactionProxyFactory.Create<ISuppressWithRequiresNewService>(midSvc, observer: null);
        var outerSvc   = new OuterWithSuppressAndRequiresNewService(hooks, midProxy);
        var outerProxy = TransactionProxyFactory.Create<IOuterWithSuppressAndRequiresNewService>(outerSvc, observer: null);

        await outerProxy.RunAsync();

        // Inner RequiresNew hook fires when its independent scope commits (inside the Suppress wrapper).
        Assert.Equal(["requiresnew-hook"], innerSvc.Fired);
        // Both outer hooks fire when the outer Required scope commits.
        // outer-hook-after would be lost if the Suppress+RequiresNew stack corrupted the AsyncLocal.
        Assert.Equal(["outer-hook-before", "outer-hook-after"], outerSvc.Fired);
    }
}
