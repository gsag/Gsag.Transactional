using System.Collections.Concurrent;
using System.Diagnostics;
using System.Transactions;
using Gsag.Transactional.Core.Attributes;
using Gsag.Transactional.Core.Extensions;
using Gsag.Transactional.Core.Hooks;
using Gsag.Transactional.Core.Observability;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

// ─── Configuration ────────────────────────────────────────────────────────────

const int ThroughputTasks = 20_000;
const int ThroughputIterationsPerTask = 50;
const int RollbackTasks = 40_000;       // 20,000 commit + 20,000 rollback
const int IsolationTasks = 20_000;
const int NestedTasks = 10_000;
const int NestedWithFailureTasks = 8_000;
const int ExceptionTasks = 15_000;
const int ExceptionPropagationTasks = 10_000;
const int ISimulationTasks = 5_000;
const int HookOrderingTasks = 6_000;

// ─── DI Setup ─────────────────────────────────────────────────────────────────

var observer = new ConcurrencyObserver();

var services = new ServiceCollection();
services.AddSingleton<ITransactionObserver>(observer);
services.AddTransactional(b => b
    .AddService<ILoadService, LoadService>()
    .AddService<IInnerService, InnerService>()
    .AddService<IOuterService, OuterService>()
    .AddService<IIsolationService, IsolationService>()
    .AddService<IExceptionService, ExceptionService>()
    .AddService<IExceptionPropagationService, ExceptionPropagationService>()
    .AddService<IInnerFailureService, InnerFailureService>()
    .AddService<INestedFailureService, NestedFailureService>()
    .AddService<IIOSimulationService, IOSimulationService>()
    .AddService<IHookOrderingService, HookOrderingService>());

var sp = services.BuildServiceProvider();
using var scope = sp.CreateScope();
var svcp = scope.ServiceProvider;

ILoadService loadProxy = svcp.GetRequiredService<ILoadService>();
IOuterService outerProxy = svcp.GetRequiredService<IOuterService>();
IIsolationService isolationProxy = svcp.GetRequiredService<IIsolationService>();
IExceptionService exceptionProxy = svcp.GetRequiredService<IExceptionService>();
IExceptionPropagationService propagationProxy = svcp.GetRequiredService<IExceptionPropagationService>();
INestedFailureService nestedFailureProxy = svcp.GetRequiredService<INestedFailureService>();
IIOSimulationService ioProxy = svcp.GetRequiredService<IIOSimulationService>();
IHookOrderingService hookOrderingProxy = svcp.GetRequiredService<IHookOrderingService>();

// ─── Header ───────────────────────────────────────────────────────────────────

AnsiConsole.Write(new FigletText("Load Test").Color(Color.Cyan1));
AnsiConsole.WriteLine();
AnsiConsole.MarkupLine("[dim]Gsag.Transactional — concurrency & load stress[/]");
AnsiConsole.MarkupLine($"[dim]throughput={ThroughputTasks:N0}×{ThroughputIterationsPerTask} | rollback={RollbackTasks:N0} | isolation={IsolationTasks:N0} | nested={NestedTasks:N0} | nested-fail={NestedWithFailureTasks:N0} | exceptions={ExceptionTasks:N0} | propagation={ExceptionPropagationTasks:N0} | io-sim={ISimulationTasks:N0}[/]");
AnsiConsole.WriteLine();

var results = new List<ScenarioResult>();
var sw = new Stopwatch();

// ─── Scenario 1: Pure throughput ──────────────────────────────────────────────

observer.Reset();
long s1Peak = 0; long s1Alloc = 0; int s1Gc0 = 0;

await AnsiConsole.Status()
    .Spinner(Spinner.Known.Dots)
    .SpinnerStyle(Style.Parse("cyan"))
    .StartAsync("[cyan]1/4[/]  Pure throughput...", async _ =>
    {
        long allocBefore = GC.GetTotalAllocatedBytes();
        int gcBefore = GC.CollectionCount(0);
        using var sampler = new PeakMemorySampler();
        sw.Restart();
        var tasks = Enumerable.Range(0, ThroughputTasks)
            .Select(_ => Task.Run(async () =>
            {
                for (int i = 0; i < ThroughputIterationsPerTask; i++)
                {
                    await loadProxy.CommitAsync();
                }
            }));
        await Task.WhenAll(tasks);
        sw.Stop();
        s1Peak = sampler.PeakBytes;
        s1Alloc = GC.GetTotalAllocatedBytes() - allocBefore;
        s1Gc0 = GC.CollectionCount(0) - gcBefore;
    });

{
    int total = ThroughputTasks * ThroughputIterationsPerTask;
    long tps = (long)(total / sw.Elapsed.TotalSeconds);
    string? error = null;
    try
    {
        AssertEq(observer.Commit, total, "Commit");
        AssertEq(observer.Rollback, 0, "Rollback");
        AssertEq(observer.Complete, total, "Complete");
    }
    catch (Exception ex) { error = ex.Message; }
    results.Add(new($"Pure throughput ({ThroughputTasks}×{ThroughputIterationsPerTask})", total, sw.Elapsed, tps, s1Peak, s1Alloc, s1Gc0, error));
}

// ─── Scenario 2: Rollback vs commit under load ───────────────────────────────

observer.Reset();
long s2Peak = 0; long s2Alloc = 0; int s2Gc0 = 0;

await AnsiConsole.Status()
    .Spinner(Spinner.Known.Dots)
    .SpinnerStyle(Style.Parse("cyan"))
    .StartAsync("[cyan]2/4[/]  Rollback vs commit...", async _ =>
    {
        int half = RollbackTasks / 2;
        long allocBefore = GC.GetTotalAllocatedBytes();
        int gcBefore = GC.CollectionCount(0);
        using var sampler = new PeakMemorySampler();
        sw.Restart();
        var tasks = Enumerable.Range(0, RollbackTasks).Select(i =>
        {
            if (i < half)
            {
                return loadProxy.CommitAsync();
            }

            return Task.Run(async () =>
            {
                try { await loadProxy.RollbackAsync(); }
                catch (InvalidOperationException) { }
            });
        });
        await Task.WhenAll(tasks);
        sw.Stop();
        s2Peak = sampler.PeakBytes;
        s2Alloc = GC.GetTotalAllocatedBytes() - allocBefore;
        s2Gc0 = GC.CollectionCount(0) - gcBefore;
    });

{
    int half = RollbackTasks / 2;
    long tps = (long)(RollbackTasks / sw.Elapsed.TotalSeconds);
    string? error = null;
    try
    {
        AssertEq(observer.Commit, half, "Commit");
        AssertEq(observer.Rollback, half, "Rollback");
        AssertEq(observer.Complete, RollbackTasks, "Complete");
    }
    catch (Exception ex) { error = ex.Message; }
    results.Add(new($"Rollback vs commit ({RollbackTasks} tasks)", RollbackTasks, sw.Elapsed, tps, s2Peak, s2Alloc, s2Gc0, error));
}

// ─── Scenario 3: Hook isolation (AsyncLocal) ─────────────────────────────────

observer.Reset();
var hookFireCount = new int[IsolationTasks];
long s3Peak = 0; long s3Alloc = 0; int s3Gc0 = 0;

await AnsiConsole.Status()
    .Spinner(Spinner.Known.Dots)
    .SpinnerStyle(Style.Parse("cyan"))
    .StartAsync("[cyan]3/4[/]  AsyncLocal isolation...", async _ =>
    {
        long allocBefore = GC.GetTotalAllocatedBytes();
        int gcBefore = GC.CollectionCount(0);
        using var sampler = new PeakMemorySampler();
        sw.Restart();
        var tasks = Enumerable.Range(0, IsolationTasks)
            .Select(i => Task.Run(async () =>
            {
                await isolationProxy.RunAsync(i, () => Interlocked.Increment(ref hookFireCount[i]));
            }));
        await Task.WhenAll(tasks);
        sw.Stop();
        s3Peak = sampler.PeakBytes;
        s3Alloc = GC.GetTotalAllocatedBytes() - allocBefore;
        s3Gc0 = GC.CollectionCount(0) - gcBefore;
    });

{
    long tps = (long)(IsolationTasks / sw.Elapsed.TotalSeconds);
    string? error = null;
    try
    {
        for (int i = 0; i < IsolationTasks; i++)
        {
            if (hookFireCount[i] != 1)
            {
                throw new Exception($"Task {i}: hook fired {hookFireCount[i]}x (expected 1) — AsyncLocal leaked");
            }
        }
    }
    catch (Exception ex) { error = ex.Message; }
    results.Add(new($"AsyncLocal isolation ({IsolationTasks} tasks)", IsolationTasks, sw.Elapsed, tps, s3Peak, s3Alloc, s3Gc0, error));
}

// ─── Scenario 4: Nested RequiresNew concurrent ────────────────────────────────

observer.Reset();
long s4Peak = 0; long s4Alloc = 0; int s4Gc0 = 0;

await AnsiConsole.Status()
    .Spinner(Spinner.Known.Dots)
    .SpinnerStyle(Style.Parse("cyan"))
    .StartAsync("[cyan]4/7[/]  Nested RequiresNew...", async _ =>
    {
        long allocBefore = GC.GetTotalAllocatedBytes();
        int gcBefore = GC.CollectionCount(0);
        using var sampler = new PeakMemorySampler();
        sw.Restart();
        var tasks = Enumerable.Range(0, NestedTasks)
            .Select(_ => Task.Run(() => outerProxy.RunWithInnerAsync()));
        await Task.WhenAll(tasks);
        sw.Stop();
        s4Peak = sampler.PeakBytes;
        s4Alloc = GC.GetTotalAllocatedBytes() - allocBefore;
        s4Gc0 = GC.CollectionCount(0) - gcBefore;
    });

{
    int totalScopes = NestedTasks * 2; // outer + inner per task
    long tps = (long)(totalScopes / sw.Elapsed.TotalSeconds);
    string? error = null;
    try
    {
        AssertEq(observer.Begin, totalScopes, "Begin (outer + inner)");
        AssertEq(observer.Commit, totalScopes, "Commit (outer + inner)");
        AssertEq(observer.Complete, totalScopes, "Complete (outer + inner)");
    }
    catch (Exception ex) { error = ex.Message; }
    results.Add(new($"Nested RequiresNew ({NestedTasks} tasks)", totalScopes, sw.Elapsed, tps, s4Peak, s4Alloc, s4Gc0, error));
}

// ─── Scenario 5: Nested RequiresNew with inner failure ────────────────────────

observer.Reset();
long s5Peak = 0; long s5Alloc = 0; int s5Gc0 = 0;

await AnsiConsole.Status()
    .Spinner(Spinner.Known.Dots)
    .SpinnerStyle(Style.Parse("cyan"))
    .StartAsync("[cyan]5/7[/]  Nested RequiresNew (inner fails)...", async _ =>
    {
        long allocBefore = GC.GetTotalAllocatedBytes();
        int gcBefore = GC.CollectionCount(0);
        using var sampler = new PeakMemorySampler();
        sw.Restart();
        var tasks = Enumerable.Range(0, NestedWithFailureTasks)
            .Select(_ => Task.Run(() => nestedFailureProxy.RunOuterWithFailingInnerAsync()));
        await Task.WhenAll(tasks);
        sw.Stop();
        s5Peak = sampler.PeakBytes;
        s5Alloc = GC.GetTotalAllocatedBytes() - allocBefore;
        s5Gc0 = GC.CollectionCount(0) - gcBefore;
    });

{
    int totalScopes = NestedWithFailureTasks * 2; // outer + inner per task
    long tps = (long)(totalScopes / sw.Elapsed.TotalSeconds);
    string? error = null;
    try
    {
        AssertEq(observer.Begin, totalScopes, "Begin (outer + inner)");
        AssertEq(observer.Commit, NestedWithFailureTasks, "Commit (outer commits, inner fails)");
        AssertEq(observer.Rollback, NestedWithFailureTasks, "Rollback (inner fails)");
        AssertEq(observer.Complete, totalScopes, "Complete (outer + inner)");
    }
    catch (Exception ex) { error = ex.Message; }
    results.Add(new($"Nested RequiresNew with failure ({NestedWithFailureTasks} tasks)", totalScopes, sw.Elapsed, tps, s5Peak, s5Alloc, s5Gc0, error));
}

// ─── Scenario 6: Exception handling (diverse exception types) ─────────────────

observer.Reset();
long s6Peak = 0; long s6Alloc = 0; int s6Gc0 = 0;

await AnsiConsole.Status()
    .Spinner(Spinner.Known.Dots)
    .SpinnerStyle(Style.Parse("cyan"))
    .StartAsync("[cyan]6/7[/]  Exception handling...", async _ =>
    {
        int third = ExceptionTasks / 3;
        long allocBefore = GC.GetTotalAllocatedBytes();
        int gcBefore = GC.CollectionCount(0);
        using var sampler = new PeakMemorySampler();
        sw.Restart();
        var tasks = Enumerable.Range(0, ExceptionTasks).Select(i =>
        {
            if (i < third)
            {
                return Task.Run(async () =>
                {
                    try { await exceptionProxy.ThrowDuringExecutionAsync(); }
                    catch { }
                });
            }
            else if (i < 2 * third)
            {
                return Task.Run(async () =>
                {
                    try { await exceptionProxy.ThrowInHookAsync(); }
                    catch { }
                });
            }
            else
            {
                return Task.Run(async () =>
                {
                    try { await exceptionProxy.ThrowCustomExceptionAsync(); }
                    catch { }
                });
            }
        });
        await Task.WhenAll(tasks);
        sw.Stop();
        s6Peak = sampler.PeakBytes;
        s6Alloc = GC.GetTotalAllocatedBytes() - allocBefore;
        s6Gc0 = GC.CollectionCount(0) - gcBefore;
    });

{
    int third = ExceptionTasks / 3;
    long tps = (long)(ExceptionTasks / sw.Elapsed.TotalSeconds);
    string? error = null;
    try
    {
        AssertEq(observer.Begin, ExceptionTasks, "Begin");
        AssertEq(observer.Commit, third, "Commit (exceptions in hooks after commit)");
        AssertEq(observer.Rollback, 2 * third, "Rollback (exceptions during execution)");
        AssertEq(observer.Complete, ExceptionTasks, "Complete");
    }
    catch (Exception ex) { error = ex.Message; }
    results.Add(new($"Exception handling ({ExceptionTasks} tasks)", ExceptionTasks, sw.Elapsed, tps, s6Peak, s6Alloc, s6Gc0, error));
}

// ─── Scenario 7: Exception propagation correctness ────────────────────────────

observer.Reset();
var rollbackObserverFired = new int[ExceptionPropagationTasks];
long s7Peak = 0; long s7Alloc = 0; int s7Gc0 = 0;

await AnsiConsole.Status()
    .Spinner(Spinner.Known.Dots)
    .SpinnerStyle(Style.Parse("cyan"))
    .StartAsync("[cyan]7/8[/]  Exception propagation...", async _ =>
    {
        long allocBefore = GC.GetTotalAllocatedBytes();
        int gcBefore = GC.CollectionCount(0);
        using var sampler = new PeakMemorySampler();
        sw.Restart();
        var tasks = Enumerable.Range(0, ExceptionPropagationTasks)
            .Select(i => Task.Run(async () =>
            {
                bool exceptionCaught = false;
                try
                {
                    await propagationProxy.ThrowAndVerifyPropagationAsync(i, rollbackObserverFired);
                }
                catch (InvalidOperationException)
                {
                    exceptionCaught = true;
                }

                if (!exceptionCaught)
                    throw new Exception($"Task {i}: Exception was not propagated");
                if (rollbackObserverFired[i] == 0)
                    throw new Exception($"Task {i}: Rollback observer did not fire");
            }));
        await Task.WhenAll(tasks);
        sw.Stop();
        s7Peak = sampler.PeakBytes;
        s7Alloc = GC.GetTotalAllocatedBytes() - allocBefore;
        s7Gc0 = GC.CollectionCount(0) - gcBefore;
    });

{
    long tps = (long)(ExceptionPropagationTasks / sw.Elapsed.TotalSeconds);
    string? error = null;
    try
    {
        AssertEq(observer.Begin, ExceptionPropagationTasks, "Begin");
        AssertEq(observer.Rollback, ExceptionPropagationTasks, "Rollback");
        AssertEq(observer.Complete, ExceptionPropagationTasks, "Complete");
        int unfiredObservers = rollbackObserverFired.Count(fired => fired == 0);
        if (unfiredObservers > 0)
            throw new Exception($"Rollback observers did not fire: {unfiredObservers} tasks");
    }
    catch (Exception ex) { error = ex.Message; }
    results.Add(new($"Exception propagation ({ExceptionPropagationTasks} tasks)", ExceptionPropagationTasks, sw.Elapsed, tps, s7Peak, s7Alloc, s7Gc0, error));
}

// ─── Scenario 8: I/O simulation with variable duration ──────────────────────

observer.Reset();
long s8Peak = 0; long s8Alloc = 0; int s8Gc0 = 0;

await AnsiConsole.Status()
    .Spinner(Spinner.Known.Dots)
    .SpinnerStyle(Style.Parse("cyan"))
    .StartAsync("[cyan]8/9[/]  I/O simulation...", async _ =>
    {
        long allocBefore = GC.GetTotalAllocatedBytes();
        int gcBefore = GC.CollectionCount(0);
        using var sampler = new PeakMemorySampler();
        sw.Restart();
        var tasks = Enumerable.Range(0, ISimulationTasks)
            .Select(_ => Task.Run(() => ioProxy.SimulateIOAsync()));
        await Task.WhenAll(tasks);
        sw.Stop();
        s8Peak = sampler.PeakBytes;
        s8Alloc = GC.GetTotalAllocatedBytes() - allocBefore;
        s8Gc0 = GC.CollectionCount(0) - gcBefore;
    });

{
    long tps = (long)(ISimulationTasks / sw.Elapsed.TotalSeconds);
    string? error = null;
    try
    {
        AssertEq(observer.Begin, ISimulationTasks, "Begin");
        AssertEq(observer.Commit, ISimulationTasks, "Commit");
        AssertEq(observer.Complete, ISimulationTasks, "Complete");
    }
    catch (Exception ex) { error = ex.Message; }
    results.Add(new($"I/O simulation ({ISimulationTasks} tasks)", ISimulationTasks, sw.Elapsed, tps, s8Peak, s8Alloc, s8Gc0, error));
}

// ─── Scenario 9: Hook ordering under concurrency ────────────────────────────

observer.Reset();
var hookOrderingFire = new int[HookOrderingTasks * 3];
long s9Peak = 0; long s9Alloc = 0; int s9Gc0 = 0;

await AnsiConsole.Status()
    .Spinner(Spinner.Known.Dots)
    .SpinnerStyle(Style.Parse("cyan"))
    .StartAsync("[cyan]9/9[/]  Hook ordering...", async _ =>
    {
        long allocBefore = GC.GetTotalAllocatedBytes();
        int gcBefore = GC.CollectionCount(0);
        using var sampler = new PeakMemorySampler();
        sw.Restart();
        var tasks = Enumerable.Range(0, HookOrderingTasks)
            .Select(i => Task.Run(() => hookOrderingProxy.ValidateHookOrderAsync(i, hookOrderingFire)));
        await Task.WhenAll(tasks);
        sw.Stop();
        s9Peak = sampler.PeakBytes;
        s9Alloc = GC.GetTotalAllocatedBytes() - allocBefore;
        s9Gc0 = GC.CollectionCount(0) - gcBefore;
    });

{
    long tps = (long)(HookOrderingTasks / sw.Elapsed.TotalSeconds);
    string? error = null;
    try
    {
        AssertEq(observer.Begin, HookOrderingTasks, "Begin");
        AssertEq(observer.Commit, HookOrderingTasks, "Commit");
        AssertEq(observer.Complete, HookOrderingTasks, "Complete");
        for (int i = 0; i < hookOrderingFire.Length; i++)
        {
            if (hookOrderingFire[i] != 1)
                throw new Exception($"Hook {i}: fired {hookOrderingFire[i]}x (expected 1)");
        }
    }
    catch (Exception ex) { error = ex.Message; }
    results.Add(new($"Hook ordering ({HookOrderingTasks} tasks)", HookOrderingTasks, sw.Elapsed, tps, s9Peak, s9Alloc, s9Gc0, error));
}

// ─── Results table ────────────────────────────────────────────────────────────

AnsiConsole.WriteLine();

var table = new Table()
    .Border(TableBorder.Rounded)
    .BorderColor(Color.Cyan1)
    .AddColumn(new TableColumn("[cyan]Scenario[/]"))
    .AddColumn(new TableColumn("[cyan]Transactions[/]").RightAligned())
    .AddColumn(new TableColumn("[cyan]Duration[/]").RightAligned())
    .AddColumn(new TableColumn("[cyan]TPS[/]").RightAligned())
    .AddColumn(new TableColumn("[cyan]Avg latency[/]").RightAligned())
    .AddColumn(new TableColumn("[cyan]Peak heap[/]").RightAligned())
    .AddColumn(new TableColumn("[cyan]Total alloc[/]").RightAligned())
    .AddColumn(new TableColumn("[cyan]GC0[/]").RightAligned())
    .AddColumn(new TableColumn("[cyan]Status[/]").Centered());

foreach (var r in results)
{
    bool ok = r.Error is null;
    string status = ok ? "[green]✓[/]" : "[red]✗[/]";
    string label = ok ? r.Label : $"{r.Label}\n[red dim]{Markup.Escape(r.Error!)}[/]";
    double avgUs = r.Transactions > 0 ? r.Elapsed.TotalMicroseconds / r.Transactions : 0;
    string peak = FormatBytes(r.PeakBytes);
    string alloc = FormatBytes(r.AllocatedBytes);
    table.AddRow(label, $"{r.Transactions:N0}", $"{r.Elapsed.TotalMilliseconds:F0} ms",
        $"{r.Tps:N0}", $"{avgUs:F1} µs", peak, alloc, $"{r.GcGen0}", status);
}

AnsiConsole.Write(table);
AnsiConsole.WriteLine();

// ─── Consistency validation ────────────────────────────────────────────────────

AnsiConsole.MarkupLine("[cyan]Transaction Lifecycle Consistency[/]");
var consistency = observer.ValidateConsistency();
AnsiConsole.MarkupLine($"Status: {consistency.Summary}");

if (!consistency.IsValid)
{
    AnsiConsole.MarkupLine("[yellow]Details:[/]");
    foreach (var err in consistency.OrphanedTransactions.Concat(consistency.IncompleteTransactions).Concat(consistency.InvalidTransitions).Take(10))
    {
        AnsiConsole.MarkupLine($"  [red]✗[/] {Markup.Escape(err)}");
    }
}
AnsiConsole.WriteLine();

bool allPassed = results.All(r => r.Error is null) && consistency.IsValid;
if (allPassed)
{
    AnsiConsole.MarkupLine("[green bold]All scenarios passed with valid transaction lifecycles.[/]");
}
else
{
    AnsiConsole.MarkupLine("[red bold]One or more scenarios failed or transaction lifecycles are invalid.[/]");
    Environment.Exit(1);
}

// ─── Helpers ──────────────────────────────────────────────────────────────────

static void AssertEq(int actual, int expected, string label)
{
    if (actual != expected)
    {
        throw new Exception($"{label}: expected {expected}, got {actual}");
    }
}

static string FormatBytes(long bytes) => bytes switch
{
    >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F1} GB",
    >= 1_048_576     => $"{bytes / 1_048_576.0:F1} MB",
    _                => $"{bytes / 1024.0:F0} KB",
};

// ─────────────────────────────────────────────────────────────────────────────
// Result record
// ─────────────────────────────────────────────────────────────────────────────

record ScenarioResult(string Label, int Transactions, TimeSpan Elapsed, long Tps, long PeakBytes, long AllocatedBytes, int GcGen0, string? Error);

// ─────────────────────────────────────────────────────────────────────────────
// Peak memory sampler — polls GC.GetTotalMemory(false) every 5 ms in background
// ─────────────────────────────────────────────────────────────────────────────

sealed class PeakMemorySampler : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private long _peak;

    public long PeakBytes => Volatile.Read(ref _peak);

    public PeakMemorySampler()
    {
        var token = _cts.Token;
        Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                long current = GC.GetTotalMemory(false);
                long prev = Volatile.Read(ref _peak);
                if (current > prev)
                {
                    Interlocked.CompareExchange(ref _peak, current, prev);
                }
                await Task.Delay(5, token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }, token);
    }

    public void Dispose() => _cts.Cancel();
}

// ─────────────────────────────────────────────────────────────────────────────
// Services
// ─────────────────────────────────────────────────────────────────────────────

interface ILoadService
{
    Task CommitAsync();
    Task RollbackAsync();
    Task<int> CommitWithResultAsync();
    ValueTask CommitValueTaskAsync();
}

class LoadService(ITransactionHooks hooks) : ILoadService
{
    [Transactional]
    public Task CommitAsync()
    {
        hooks.AfterCommit(() => { });
        return Task.CompletedTask;
    }

    [Transactional]
    public Task RollbackAsync()
    {
        hooks.AfterRollback(() => { });
        throw new InvalidOperationException("forced rollback");
    }

    [Transactional]
    public Task<int> CommitWithResultAsync() => Task.FromResult(42);

    [Transactional]
    public ValueTask CommitValueTaskAsync() => ValueTask.CompletedTask;
}

interface IInnerService
{
    Task RunAsync();
}

class InnerService(ITransactionHooks hooks) : IInnerService
{
    [Transactional(Propagation = TransactionScopeOption.RequiresNew)]
    public Task RunAsync()
    {
        hooks.AfterCommit(() => { });
        return Task.CompletedTask;
    }
}

interface IOuterService
{
    Task RunWithInnerAsync();
}

class OuterService(ITransactionHooks hooks, IInnerService inner) : IOuterService
{
    [Transactional]
    public async Task RunWithInnerAsync()
    {
        hooks.AfterCommit(() => { });
        await inner.RunAsync();
    }
}

interface IIsolationService
{
    Task RunAsync(int taskId, Action onCommit);
}

class IsolationService(ITransactionHooks hooks) : IIsolationService
{
    [Transactional]
    public Task RunAsync(int taskId, Action onCommit)
    {
        hooks.AfterCommit(onCommit);
        return Task.CompletedTask;
    }
}

interface IExceptionService
{
    Task ThrowDuringExecutionAsync();
    Task ThrowInHookAsync();
    Task ThrowCustomExceptionAsync();
}

class ExceptionService(ITransactionHooks hooks) : IExceptionService
{
    [Transactional]
    public Task ThrowDuringExecutionAsync()
    {
        hooks.AfterCommit(() => { });
        throw new InvalidOperationException("Exception during transaction execution");
    }

    [Transactional]
    public Task ThrowInHookAsync()
    {
        hooks.AfterCommit(() => throw new ArgumentException("Exception in AfterCommit hook"));
        return Task.CompletedTask;
    }

    [Transactional]
    public Task ThrowCustomExceptionAsync()
    {
        hooks.AfterCommit(() => { });
        throw new TimeoutException("Custom exception during transaction");
    }
}

interface IExceptionPropagationService
{
    Task ThrowAndVerifyPropagationAsync(int taskId, int[] rollbackObserverFired);
}

class ExceptionPropagationService(ITransactionHooks hooks) : IExceptionPropagationService
{
    [Transactional]
    public Task ThrowAndVerifyPropagationAsync(int taskId, int[] rollbackObserverFired)
    {
        hooks.AfterRollback(() =>
        {
            Interlocked.Increment(ref rollbackObserverFired[taskId]);
        });
        throw new InvalidOperationException($"Task {taskId}: Exception for propagation test");
    }
}

interface INestedFailureService
{
    Task RunOuterWithFailingInnerAsync();
}

interface IInnerFailureService
{
    Task RunAndFailAsync();
}

class InnerFailureService(ITransactionHooks hooks) : IInnerFailureService
{
    [Transactional(Propagation = TransactionScopeOption.RequiresNew)]
    public Task RunAndFailAsync()
    {
        hooks.AfterRollback(() => { });
        throw new InvalidOperationException("Inner transaction failed intentionally");
    }
}

class NestedFailureService(ITransactionHooks hooks, IInnerFailureService inner) : INestedFailureService
{
    [Transactional]
    public async Task RunOuterWithFailingInnerAsync()
    {
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
    Task SimulateIOAsync();
}

class IOSimulationService(ITransactionHooks hooks) : IIOSimulationService
{
    private static readonly Random _random = new();

    [Transactional]
    public async Task SimulateIOAsync()
    {
        int delayMs = _random.Next(1, 11);
        await Task.Delay(delayMs);
        hooks.AfterCommit(() => { });
    }
}

interface IHookOrderingService
{
    Task ValidateHookOrderAsync(int taskId, int[] hookFireCount);
}

class HookOrderingService(ITransactionHooks hooks) : IHookOrderingService
{
    [Transactional]
    public Task ValidateHookOrderAsync(int taskId, int[] hookFireCount)
    {
        int baseIndex = taskId * 3;
        hooks.AfterCommit(() => Interlocked.Increment(ref hookFireCount[baseIndex]));
        hooks.AfterCommit(() => Interlocked.Increment(ref hookFireCount[baseIndex + 1]));
        hooks.AfterCommit(() => Interlocked.Increment(ref hookFireCount[baseIndex + 2]));
        return Task.CompletedTask;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Observer
// ─────────────────────────────────────────────────────────────────────────────

sealed class ConcurrencyObserver : ITransactionObserver
{
    private int _begin, _commit, _rollback, _complete;
    private long _txnIdCounter;
    private readonly ConcurrentDictionary<long, TransactionLifecycle> _lifetimes = new();
    private readonly AsyncLocal<Stack<long>> _txnStack = new();

    public int Begin => _begin;
    public int Commit => _commit;
    public int Rollback => _rollback;
    public int Complete => _complete;

    private Stack<long> TxnStack => _txnStack.Value ??= new();

    public void Reset()
    {
        _begin = _commit = _rollback = _complete = 0;
        _txnIdCounter = 0;
        _lifetimes.Clear();
    }

    public void OnBegin(TransactionInfo info)
    {
        long id = Interlocked.Increment(ref _txnIdCounter);
        _lifetimes[id] = new TransactionLifecycle { Id = id, Info = info, BeganAt = DateTime.UtcNow };
        TxnStack.Push(id);
        Interlocked.Increment(ref _begin);
    }

    public void OnCommit(TransactionInfo info, TimeSpan elapsed)
    {
        var stack = TxnStack;
        long? id = stack.Count > 0 ? stack.Peek() : null;
        if (id.HasValue && _lifetimes.TryGetValue(id.Value, out var lifecycle))
        {
            lifecycle.CommitAt = DateTime.UtcNow;
            lifecycle.CommitElapsed = elapsed;
        }
        Interlocked.Increment(ref _commit);
    }

    public void OnRollback(TransactionInfo info, Exception exception, TimeSpan elapsed)
    {
        var stack = TxnStack;
        long? id = stack.Count > 0 ? stack.Peek() : null;
        if (id.HasValue && _lifetimes.TryGetValue(id.Value, out var lifecycle))
        {
            lifecycle.RollbackAt = DateTime.UtcNow;
            lifecycle.RollbackElapsed = elapsed;
        }
        Interlocked.Increment(ref _rollback);
    }

    public void OnComplete(TransactionInfo info, bool committed, TimeSpan elapsed)
    {
        var stack = TxnStack;
        long? id = stack.Count > 0 ? stack.Pop() : null;
        if (id.HasValue && _lifetimes.TryGetValue(id.Value, out var lifecycle))
        {
            lifecycle.CompletedAt = DateTime.UtcNow;
            lifecycle.CompleteElapsed = elapsed;
            lifecycle.WasCommitted = committed;
        }
        Interlocked.Increment(ref _complete);
    }

    public ConsistencyCheckResult ValidateConsistency()
    {
        var result = new ConsistencyCheckResult();

        foreach (var kvp in _lifetimes)
        {
            var lifecycle = kvp.Value;

            if (lifecycle.BeganAt == default)
            {
                result.OrphanedTransactions.Add($"TXN {kvp.Key}: No Begin event recorded");
                continue;
            }

            if (lifecycle.CompletedAt == default)
            {
                result.IncompleteTransactions.Add(
                    $"TXN {kvp.Key}: Began but never completed ({lifecycle.Info.MethodName})");
                continue;
            }

            if (lifecycle.CommitAt == default && lifecycle.RollbackAt == default)
            {
                result.IncompleteTransactions.Add(
                    $"TXN {kvp.Key}: Completed but no Commit or Rollback event");
                continue;
            }

            if (lifecycle.CommitAt != default && lifecycle.RollbackAt != default)
            {
                result.InvalidTransitions.Add(
                    $"TXN {kvp.Key}: Both Commit and Rollback events recorded (invalid state)");
            }

            if (lifecycle.WasCommitted && lifecycle.RollbackAt != default)
            {
                result.InvalidTransitions.Add(
                    $"TXN {kvp.Key}: Marked as committed but has Rollback event");
            }

            if (!lifecycle.WasCommitted && lifecycle.CommitAt != default)
            {
                result.InvalidTransitions.Add(
                    $"TXN {kvp.Key}: Marked as rolled back but has Commit event");
            }
        }

        return result;
    }

    private class TransactionLifecycle
    {
        public long Id { get; set; }
        public TransactionInfo Info { get; set; } = null!;
        public DateTime BeganAt { get; set; }
        public DateTime CommitAt { get; set; }
        public DateTime RollbackAt { get; set; }
        public DateTime CompletedAt { get; set; }
        public TimeSpan CommitElapsed { get; set; }
        public TimeSpan RollbackElapsed { get; set; }
        public TimeSpan CompleteElapsed { get; set; }
        public bool WasCommitted { get; set; }
    }
}

record ConsistencyCheckResult
{
    public List<string> OrphanedTransactions { get; } = new();
    public List<string> IncompleteTransactions { get; } = new();
    public List<string> InvalidTransitions { get; } = new();

    public bool IsValid =>
        OrphanedTransactions.Count == 0 &&
        IncompleteTransactions.Count == 0 &&
        InvalidTransitions.Count == 0;

    public string Summary
    {
        get
        {
            if (IsValid) return "✓ All transactions valid";
            var errors = new List<string>();
            if (OrphanedTransactions.Count > 0)
                errors.Add($"Orphaned: {OrphanedTransactions.Count}");
            if (IncompleteTransactions.Count > 0)
                errors.Add($"Incomplete: {IncompleteTransactions.Count}");
            if (InvalidTransitions.Count > 0)
                errors.Add($"Invalid: {InvalidTransitions.Count}");
            return $"✗ {string.Join(", ", errors)}";
        }
    }
}
