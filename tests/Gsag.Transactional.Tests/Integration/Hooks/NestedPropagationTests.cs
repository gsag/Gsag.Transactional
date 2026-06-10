using System.Transactions;
using Gsag.Transactional.Core.Attributes;
using Gsag.Transactional.Core.Hooks;
using Gsag.Transactional.Core.Proxy;
using Xunit;

namespace Gsag.Transactional.Tests.Integration.Hooks;

// ---------------------------------------------------------------------------
// 4-level nesting: Required (L1) → RequiresNew (L2) → Required-joins-L2 (L3) → RequiresNew (L4)
// ---------------------------------------------------------------------------

public interface IDeepL4Service
{
    Task RunAsync();
}

public class DeepL4Service : IDeepL4Service
{
    private readonly ITransactionHooks _hooks;
    public List<string> Fired { get; } = [];
    public DeepL4Service(ITransactionHooks hooks) => _hooks = hooks;

    [Transactional(Propagation = TransactionScopeOption.RequiresNew)]
    public async Task RunAsync()
    {
        await Task.CompletedTask;
        _hooks.AfterCommit(() => Fired.Add("l4-hook"));
    }
}

public interface IDeepL3Service
{
    Task RunAsync();
}

public class DeepL3Service : IDeepL3Service
{
    private readonly ITransactionHooks _hooks;
    private readonly IDeepL4Service _l4;
    public List<string> Fired { get; } = [];

    public DeepL3Service(ITransactionHooks hooks, IDeepL4Service l4)
    {
        _hooks = hooks;
        _l4 = l4;
    }

    [Transactional]
    public async Task RunAsync()
    {
        _hooks.AfterCommit(() => Fired.Add("l3-hook-before"));
        await _l4.RunAsync();
        _hooks.AfterCommit(() => Fired.Add("l3-hook-after"));
    }
}

public interface IDeepL2Service
{
    Task RunAsync();
}

public class DeepL2Service : IDeepL2Service
{
    private readonly ITransactionHooks _hooks;
    private readonly IDeepL3Service _l3;
    public List<string> Fired { get; } = [];

    public DeepL2Service(ITransactionHooks hooks, IDeepL3Service l3)
    {
        _hooks = hooks;
        _l3 = l3;
    }

    [Transactional(Propagation = TransactionScopeOption.RequiresNew)]
    public async Task RunAsync()
    {
        _hooks.AfterCommit(() => Fired.Add("l2-hook-before"));
        await _l3.RunAsync();
        _hooks.AfterCommit(() => Fired.Add("l2-hook-after"));
    }
}

public interface IDeepL1Service
{
    Task RunAsync();
}

public class DeepL1Service : IDeepL1Service
{
    private readonly ITransactionHooks _hooks;
    private readonly IDeepL2Service _l2;
    public List<string> Fired { get; } = [];

    public DeepL1Service(ITransactionHooks hooks, IDeepL2Service l2)
    {
        _hooks = hooks;
        _l2 = l2;
    }

    [Transactional]
    public async Task RunAsync()
    {
        _hooks.AfterCommit(() => Fired.Add("l1-hook-before"));
        await _l2.RunAsync();
        _hooks.AfterCommit(() => Fired.Add("l1-hook-after"));
    }
}

// ---------------------------------------------------------------------------
// ClearScope restoration — ValueTask and ValueTask<T> inner scopes (AsyncHandler lines 69, 93)
// ---------------------------------------------------------------------------

// Inner service returning ValueTask from a RequiresNew scope.
// Exercises ExecuteValueTask, which must call ClearScope synchronously so the outer
// execution context sees the outer hook collection when it resumes after the await.
public interface IInnerValueTaskService
{
    [Transactional(Propagation = TransactionScopeOption.RequiresNew)]
    ValueTask RunAsync();
}

public class InnerValueTaskService : IInnerValueTaskService
{
    private readonly ITransactionHooks _hooks;
    public List<string> Fired { get; } = [];
    public InnerValueTaskService(ITransactionHooks hooks) => _hooks = hooks;

    public async ValueTask RunAsync()
    {
        await Task.CompletedTask;
        _hooks.AfterCommit(() => Fired.Add("inner-hook"));
    }
}

// Outer service returning Task. After awaiting the inner ValueTask call, it registers
// a hook that must land in the outer scope — not in the inner's now-dead collection.
public interface IOuterCallingValueTaskService
{
    [Transactional]
    Task RunAsync();
}

public class OuterCallingValueTaskService : IOuterCallingValueTaskService
{
    private readonly ITransactionHooks _hooks;
    private readonly IInnerValueTaskService _inner;
    public List<string> Fired { get; } = [];

    public OuterCallingValueTaskService(ITransactionHooks hooks, IInnerValueTaskService inner)
    {
        _hooks = hooks;
        _inner = inner;
    }

    public async Task RunAsync()
    {
        await _inner.RunAsync();
        // Registered after the inner RequiresNew scope completes. If ClearScope was not called
        // synchronously in ExecuteValueTask, _current would still point to the inner's dead
        // hook collection and this hook would be lost at outer commit.
        _hooks.AfterCommit(() => Fired.Add("outer-hook-after-inner"));
    }
}

// Inner service returning ValueTask<int> from a RequiresNew scope.
// Exercises ExecuteValueTaskGeneric, which must also call ClearScope synchronously.
public interface IInnerValueTaskGenericService
{
    [Transactional(Propagation = TransactionScopeOption.RequiresNew)]
    ValueTask<int> RunAsync();
}

public class InnerValueTaskGenericService : IInnerValueTaskGenericService
{
    private readonly ITransactionHooks _hooks;
    public List<string> Fired { get; } = [];
    public InnerValueTaskGenericService(ITransactionHooks hooks) => _hooks = hooks;

    public async ValueTask<int> RunAsync()
    {
        await Task.CompletedTask;
        _hooks.AfterCommit(() => Fired.Add("inner-hook"));
        return 42;
    }
}

public interface IOuterCallingValueTaskGenericService
{
    [Transactional]
    Task RunAsync();
}

public class OuterCallingValueTaskGenericService : IOuterCallingValueTaskGenericService
{
    private readonly ITransactionHooks _hooks;
    private readonly IInnerValueTaskGenericService _inner;
    public List<string> Fired { get; } = [];

    public OuterCallingValueTaskGenericService(ITransactionHooks hooks, IInnerValueTaskGenericService inner)
    {
        _hooks = hooks;
        _inner = inner;
    }

    public async Task RunAsync()
    {
        await _inner.RunAsync();
        _hooks.AfterCommit(() => Fired.Add("outer-hook-after-inner"));
    }
}

// ---------------------------------------------------------------------------
// 4-level independent scopes: Required (L1) → RequiresNew (L2) → RequiresNew (L3) → RequiresNew (L4)
// ---------------------------------------------------------------------------

public interface IIndependentL4Service
{
    Task RunAsync();
}

public class IndependentL4Service : IIndependentL4Service
{
    private readonly ITransactionHooks _hooks;
    public List<string> Fired { get; } = [];
    public IndependentL4Service(ITransactionHooks hooks) => _hooks = hooks;

    [Transactional(Propagation = TransactionScopeOption.RequiresNew)]
    public async Task RunAsync()
    {
        await Task.CompletedTask;
        _hooks.AfterCommit(() => Fired.Add("l4-independent"));
    }
}

public interface IIndependentL3Service
{
    Task RunAsync();
}

public class IndependentL3Service : IIndependentL3Service
{
    private readonly ITransactionHooks _hooks;
    private readonly IIndependentL4Service _l4;
    public List<string> Fired { get; } = [];

    public IndependentL3Service(ITransactionHooks hooks, IIndependentL4Service l4)
    {
        _hooks = hooks;
        _l4 = l4;
    }

    [Transactional(Propagation = TransactionScopeOption.RequiresNew)]
    public async Task RunAsync()
    {
        await _l4.RunAsync();
        _hooks.AfterCommit(() => Fired.Add("l3-independent"));
    }
}

public interface IIndependentL2Service
{
    Task RunAsync();
}

public class IndependentL2Service : IIndependentL2Service
{
    private readonly ITransactionHooks _hooks;
    private readonly IIndependentL3Service _l3;
    public List<string> Fired { get; } = [];

    public IndependentL2Service(ITransactionHooks hooks, IIndependentL3Service l3)
    {
        _hooks = hooks;
        _l3 = l3;
    }

    [Transactional(Propagation = TransactionScopeOption.RequiresNew)]
    public async Task RunAsync()
    {
        await _l3.RunAsync();
        _hooks.AfterCommit(() => Fired.Add("l2-independent"));
    }
}

public interface IIndependentL1Service
{
    Task RunAsync();
}

public class IndependentL1Service : IIndependentL1Service
{
    private readonly ITransactionHooks _hooks;
    private readonly IIndependentL2Service _l2;
    public List<string> Fired { get; } = [];

    public IndependentL1Service(ITransactionHooks hooks, IIndependentL2Service l2)
    {
        _hooks = hooks;
        _l2 = l2;
    }

    [Transactional]
    public async Task RunAsync()
    {
        await _l2.RunAsync();
        _hooks.AfterCommit(() => Fired.Add("l1-independent"));
    }
}

// ---------------------------------------------------------------------------

public class NestedPropagationTests
{
    /// <summary>
    /// 4-level: Required (L1) → RequiresNew (L2) → Required-joins-L2 (L3) → RequiresNew (L4).
    ///
    /// L4 commits independently → its hook fires immediately.
    /// L3 joins L2's scope → its hooks accumulate in L2's collection.
    /// L2 commits → L2 + L3 hooks fire.
    /// L1 commits → L1 hooks fire (before and after the entire L2 subtree).
    ///
    /// Verifies the AsyncLocal stack is correctly maintained across 4 levels of mixed propagation.
    /// </summary>
    [Fact]
    public async Task FourLevel_RequiredRequiresNewJoiningRequiresNew_AllHooksFire()
    {
        var hooks = new TransactionHooks();

        var l4Svc = new DeepL4Service(hooks);
        var l4Proxy = TransactionProxyFactory.Create<IDeepL4Service>(l4Svc, observer: null);

        var l3Svc = new DeepL3Service(hooks, l4Proxy);
        var l3Proxy = TransactionProxyFactory.Create<IDeepL3Service>(l3Svc, observer: null);

        var l2Svc = new DeepL2Service(hooks, l3Proxy);
        var l2Proxy = TransactionProxyFactory.Create<IDeepL2Service>(l2Svc, observer: null);

        var l1Svc = new DeepL1Service(hooks, l2Proxy);
        var l1Proxy = TransactionProxyFactory.Create<IDeepL1Service>(l1Svc, observer: null);

        await l1Proxy.RunAsync();

        // L4 fires when its independent RequiresNew scope commits (inside the L2 call).
        Assert.Equal(["l4-hook"], l4Svc.Fired);
        // L3 joined L2's scope — its hooks merged into L2's collection and fire at L2 commit.
        Assert.Equal(["l3-hook-before", "l3-hook-after"], l3Svc.Fired);
        // L2 hooks fire at L2 commit (including merged L3 hooks, but tracked separately).
        Assert.Equal(["l2-hook-before", "l2-hook-after"], l2Svc.Fired);
        // L1 hooks fire when L1 commits — after the entire L2 subtree has resolved.
        // l1-hook-after would be lost if the RequiresNew stack corrupted the AsyncLocal at L1.
        Assert.Equal(["l1-hook-before", "l1-hook-after"], l1Svc.Fired);
    }

    /// <summary>
    /// Verifies that <c>ClearScope</c> is called synchronously in <c>ExecuteValueTask</c>
    /// so the caller's execution context is restored before it resumes after the await.
    /// If <c>ClearScope</c> is skipped, the hook registered by the outer method after the inner call
    /// would be added to the inner's now-dead collection and would not fire at outer commit.
    /// </summary>
    [Fact]
    public async Task ClearScope_AfterValueTask_RestoredInCallerContext()
    {
        var hooks = new TransactionHooks();

        var innerSvc = new InnerValueTaskService(hooks);
        var innerProxy = TransactionProxyFactory.Create<IInnerValueTaskService>(innerSvc, observer: null);

        var outerSvc = new OuterCallingValueTaskService(hooks, innerProxy);
        var outerProxy = TransactionProxyFactory.Create<IOuterCallingValueTaskService>(outerSvc, observer: null);

        await outerProxy.RunAsync();

        Assert.Contains("inner-hook", innerSvc.Fired);
        Assert.Contains("outer-hook-after-inner", outerSvc.Fired);
    }

    /// <summary>
    /// Verifies that <c>ClearScope</c> is called synchronously in <c>ExecuteValueTaskGeneric</c>
    /// so the caller's execution context is restored before it resumes after the await.
    /// </summary>
    [Fact]
    public async Task ClearScope_AfterValueTaskGeneric_RestoredInCallerContext()
    {
        var hooks = new TransactionHooks();

        var innerSvc = new InnerValueTaskGenericService(hooks);
        var innerProxy = TransactionProxyFactory.Create<IInnerValueTaskGenericService>(innerSvc, observer: null);

        var outerSvc = new OuterCallingValueTaskGenericService(hooks, innerProxy);
        var outerProxy = TransactionProxyFactory.Create<IOuterCallingValueTaskGenericService>(outerSvc, observer: null);

        await outerProxy.RunAsync();

        Assert.Contains("inner-hook", innerSvc.Fired);
        Assert.Contains("outer-hook-after-inner", outerSvc.Fired);
    }

    /// <summary>
    /// 4-level independent scopes: Required (L1) → RequiresNew (L2) → RequiresNew (L3) → RequiresNew (L4).
    ///
    /// Each RequiresNew scope commits independently in reverse order (L4, then L3, then L2, then L1).
    /// Each layer's AfterCommit hook fires at its own commit — none should be lost.
    /// </summary>
    [Fact]
    public async Task FourLevel_AllRequiresNew_EachScopeHookFiresAtOwnCommit()
    {
        var hooks = new TransactionHooks();

        var l4Svc = new IndependentL4Service(hooks);
        var l4Proxy = TransactionProxyFactory.Create<IIndependentL4Service>(l4Svc, observer: null);

        var l3Svc = new IndependentL3Service(hooks, l4Proxy);
        var l3Proxy = TransactionProxyFactory.Create<IIndependentL3Service>(l3Svc, observer: null);

        var l2Svc = new IndependentL2Service(hooks, l3Proxy);
        var l2Proxy = TransactionProxyFactory.Create<IIndependentL2Service>(l2Svc, observer: null);

        var l1Svc = new IndependentL1Service(hooks, l2Proxy);
        var l1Proxy = TransactionProxyFactory.Create<IIndependentL1Service>(l1Svc, observer: null);

        await l1Proxy.RunAsync();

        Assert.Equal(["l4-independent"], l4Svc.Fired);
        Assert.Equal(["l3-independent"], l3Svc.Fired);
        Assert.Equal(["l2-independent"], l2Svc.Fired);
        Assert.Equal(["l1-independent"], l1Svc.Fired);
    }
}
