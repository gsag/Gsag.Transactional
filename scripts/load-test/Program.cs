using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Transactions;
using Gsag.Transactional.Core.Attributes;
using Gsag.Transactional.Core.Extensions;
using Gsag.Transactional.Core.Hooks;
using Gsag.Transactional.Core.Observability;
using LoadTest.Data;
using LoadTest.Helpers;
using LoadTest.Observers;
using LoadTest.Services;
using LoadTest.System;
using LoadTest.Validation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

// ─── Configuration ────────────────────────────────────────────────────────────

const int TotalScenarios = 9;
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

var scenarioIndex = 1;

// ─── System Information ────────────────────────────────────────────────────────

var systemInfo = SystemInfoCollector.Collect();

// ─── DI Setup ─────────────────────────────────────────────────────────────────

var observer = new ConcurrencyObserver();
var accumulator = new LifecycleAccumulator();

var services = new ServiceCollection();
services.AddSingleton<ITransactionObserver>(observer);
services.AddDbContextFactory<LoadTestDbContext>(options => options.UseNpgsql("Host=localhost;Port=5432;Database=loadtest;Username=loadtest;Password=loadtest123;"));
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

var dbFactory = sp.GetRequiredService<IDbContextFactory<LoadTestDbContext>>();
using (var setupDb = dbFactory.CreateDbContext())
{
    await setupDb.Database.EnsureCreatedAsync();
}

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
AnsiConsole.MarkupLine("[dim]Gsag.Transactional — concurrency & load stress with PostgreSQL[/]");
AnsiConsole.MarkupLine($"[dim]throughput={ThroughputTasks:N0}×{ThroughputIterationsPerTask} | rollback={RollbackTasks:N0} | isolation={IsolationTasks:N0} | nested={NestedTasks:N0} | nested-fail={NestedWithFailureTasks:N0} | exceptions={ExceptionTasks:N0} | propagation={ExceptionPropagationTasks:N0} | io-sim={ISimulationTasks:N0}[/]");
AnsiConsole.WriteLine();

// ─── System Information Display ────────────────────────────────────────────────

DisplaySystemInfo(systemInfo);
AnsiConsole.WriteLine();

var results = new List<ScenarioResult>();
var sw = new Stopwatch();

// ─── Scenario 1: Pure throughput ──────────────────────────────────────────────

observer.Reset();
long s1Peak = 0; long s1Alloc = 0; int s1Gc0 = 0;

await AnsiConsole.Status()
    .Spinner(Spinner.Known.Dots)
    .SpinnerStyle(Style.Parse("cyan"))
    .StartAsync($"[cyan]{scenarioIndex++}/{TotalScenarios}[/]  Pure throughput with bank ops...", async _ =>
    {
        await ClearDatabase(dbFactory);
        long allocBefore = GC.GetTotalAllocatedBytes();
        int gcBefore = GC.CollectionCount(0);
        using var sampler = new PeakMemorySampler();
        sw.Restart();
        var tasks = Enumerable.Range(0, ThroughputTasks)
            .Select(_ => Task.Run(async () =>
            {
                for (int i = 0; i < ThroughputIterationsPerTask; i++)
                {
                    await loadProxy.InsertAccountAsync();
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
    accumulator.CaptureErrors(1, observer.ValidateConsistency());
}

// ─── Scenario 2: Rollback vs commit under load ───────────────────────────────

observer.Reset();
long s2Peak = 0; long s2Alloc = 0; int s2Gc0 = 0;

await AnsiConsole.Status()
    .Spinner(Spinner.Known.Dots)
    .SpinnerStyle(Style.Parse("cyan"))
    .StartAsync($"[cyan]{scenarioIndex++}/{TotalScenarios}[/]  Rollback vs commit with bank...", async _ =>
    {
        await ClearDatabase(dbFactory);
        int half = RollbackTasks / 2;
        long allocBefore = GC.GetTotalAllocatedBytes();
        int gcBefore = GC.CollectionCount(0);
        using var sampler = new PeakMemorySampler();
        sw.Restart();
        var tasks = Enumerable.Range(0, RollbackTasks).Select(i =>
        {
            if (i < half)
            {
                return loadProxy.InsertAccountAsync();
            }

            return Task.Run(async () =>
            {
                try { await loadProxy.InsertAccountFailAsync(); }
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
    accumulator.CaptureErrors(2, observer.ValidateConsistency());
}

// ─── Scenario 3: Hook isolation (AsyncLocal) with bank ─────────────────────

observer.Reset();
var hookFireCount = new int[IsolationTasks];
long s3Peak = 0; long s3Alloc = 0; int s3Gc0 = 0;

await AnsiConsole.Status()
    .Spinner(Spinner.Known.Dots)
    .SpinnerStyle(Style.Parse("cyan"))
    .StartAsync($"[cyan]{scenarioIndex++}/{TotalScenarios}[/]  AsyncLocal isolation with bank...", async _ =>
    {
        await ClearDatabase(dbFactory);
        long allocBefore = GC.GetTotalAllocatedBytes();
        int gcBefore = GC.CollectionCount(0);
        using var sampler = new PeakMemorySampler();
        sw.Restart();
        var tasks = Enumerable.Range(0, IsolationTasks)
            .Select(i => Task.Run(async () =>
            {
                await isolationProxy.UpdateAccountAsync(i, () => Interlocked.Increment(ref hookFireCount[i]));
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
    accumulator.CaptureErrors(3, observer.ValidateConsistency());
}

// ─── Scenario 4: Nested RequiresNew with bank ops ─────────────────────────────

observer.Reset();
long s4Peak = 0; long s4Alloc = 0; int s4Gc0 = 0;

await AnsiConsole.Status()
    .Spinner(Spinner.Known.Dots)
    .SpinnerStyle(Style.Parse("cyan"))
    .StartAsync($"[cyan]{scenarioIndex++}/{TotalScenarios}[/]  Nested RequiresNew with bank...", async _ =>
    {
        await ClearDatabase(dbFactory);
        long allocBefore = GC.GetTotalAllocatedBytes();
        int gcBefore = GC.CollectionCount(0);
        using var sampler = new PeakMemorySampler();
        sw.Restart();
        var tasks = Enumerable.Range(0, NestedTasks)
            .Select(_ => Task.Run(() => outerProxy.RunWithInnerBankAsync()));
        await Task.WhenAll(tasks);
        sw.Stop();
        s4Peak = sampler.PeakBytes;
        s4Alloc = GC.GetTotalAllocatedBytes() - allocBefore;
        s4Gc0 = GC.CollectionCount(0) - gcBefore;
    });

{
    int totalScopes = NestedTasks * 2;
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
    accumulator.CaptureErrors(4, observer.ValidateConsistency());
}

// ─── Scenario 5: Nested RequiresNew with inner failure ────────────────────────

observer.Reset();
long s5Peak = 0; long s5Alloc = 0; int s5Gc0 = 0;

await AnsiConsole.Status()
    .Spinner(Spinner.Known.Dots)
    .SpinnerStyle(Style.Parse("cyan"))
    .StartAsync($"[cyan]{scenarioIndex++}/{TotalScenarios}[/]  Nested RequiresNew (inner fails) with bank...", async _ =>
    {
        await ClearDatabase(dbFactory);
        long allocBefore = GC.GetTotalAllocatedBytes();
        int gcBefore = GC.CollectionCount(0);
        using var sampler = new PeakMemorySampler();
        sw.Restart();
        var tasks = Enumerable.Range(0, NestedWithFailureTasks)
            .Select(_ => Task.Run(() => nestedFailureProxy.RunOuterWithFailingInnerBankAsync()));
        await Task.WhenAll(tasks);
        sw.Stop();
        s5Peak = sampler.PeakBytes;
        s5Alloc = GC.GetTotalAllocatedBytes() - allocBefore;
        s5Gc0 = GC.CollectionCount(0) - gcBefore;
    });

{
    int totalScopes = NestedWithFailureTasks * 2;
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
    accumulator.CaptureErrors(5, observer.ValidateConsistency());
}

// ─── Scenario 6: Exception handling with bank ─────────────────────────────────

observer.Reset();
long s6Peak = 0; long s6Alloc = 0; int s6Gc0 = 0;

await AnsiConsole.Status()
    .Spinner(Spinner.Known.Dots)
    .SpinnerStyle(Style.Parse("cyan"))
    .StartAsync($"[cyan]{scenarioIndex++}/{TotalScenarios}[/]  Exception handling with bank...", async _ =>
    {
        await ClearDatabase(dbFactory);
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
                    try { await exceptionProxy.ThrowDuringExecutionBankAsync(); }
                    catch { }
                });
            }
            else if (i < 2 * third)
            {
                return Task.Run(async () =>
                {
                    try { await exceptionProxy.ThrowInHookBankAsync(); }
                    catch { }
                });
            }
            else
            {
                return Task.Run(async () =>
                {
                    try { await exceptionProxy.ThrowCustomExceptionBankAsync(); }
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
    accumulator.CaptureErrors(6, observer.ValidateConsistency());
}

// ─── Scenario 7: Exception propagation correctness ────────────────────────────

observer.Reset();
var rollbackObserverFired = new int[ExceptionPropagationTasks];
long s7Peak = 0; long s7Alloc = 0; int s7Gc0 = 0;

await AnsiConsole.Status()
    .Spinner(Spinner.Known.Dots)
    .SpinnerStyle(Style.Parse("cyan"))
    .StartAsync($"[cyan]{scenarioIndex++}/{TotalScenarios}[/]  Exception propagation with bank...", async _ =>
    {
        await ClearDatabase(dbFactory);
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
                    await propagationProxy.ThrowAndVerifyPropagationBankAsync(i, rollbackObserverFired);
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
    accumulator.CaptureErrors(7, observer.ValidateConsistency());
}

// ─── Scenario 8: I/O simulation with variable duration ──────────────────────

observer.Reset();
long s8Peak = 0; long s8Alloc = 0; int s8Gc0 = 0;

await AnsiConsole.Status()
    .Spinner(Spinner.Known.Dots)
    .SpinnerStyle(Style.Parse("cyan"))
    .StartAsync($"[cyan]{scenarioIndex++}/{TotalScenarios}[/]  I/O simulation with bank...", async _ =>
    {
        await ClearDatabase(dbFactory);
        long allocBefore = GC.GetTotalAllocatedBytes();
        int gcBefore = GC.CollectionCount(0);
        using var sampler = new PeakMemorySampler();
        sw.Restart();
        var tasks = Enumerable.Range(0, ISimulationTasks)
            .Select(_ => Task.Run(() => ioProxy.SimulateIOWithBankAsync()));
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
    accumulator.CaptureErrors(8, observer.ValidateConsistency());
}

// ─── Scenario 9: Hook ordering under concurrency with bank ────────────────────

observer.Reset();
var hookOrderingFire = new int[HookOrderingTasks * 3];
long s9Peak = 0; long s9Alloc = 0; int s9Gc0 = 0;

await AnsiConsole.Status()
    .Spinner(Spinner.Known.Dots)
    .SpinnerStyle(Style.Parse("cyan"))
    .StartAsync($"[cyan]{scenarioIndex++}/{TotalScenarios}[/]  Hook ordering with bank...", async _ =>
    {
        await ClearDatabase(dbFactory);
        long allocBefore = GC.GetTotalAllocatedBytes();
        int gcBefore = GC.CollectionCount(0);
        using var sampler = new PeakMemorySampler();
        sw.Restart();
        var tasks = Enumerable.Range(0, HookOrderingTasks)
            .Select(i => Task.Run(() => hookOrderingProxy.ValidateHookOrderBankAsync(i, hookOrderingFire)));
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
    accumulator.CaptureErrors(9, observer.ValidateConsistency());
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
if (accumulator.HasErrors)
{
    AnsiConsole.MarkupLine($"[red bold]✗ Lifecycle errors found across scenarios:[/]");
    foreach (var (scenario, error) in accumulator.Errors.Take(20))
    {
        AnsiConsole.MarkupLine($"  [red]Scenario {scenario}:[/] {Markup.Escape(error)}");
    }
    if (accumulator.Errors.Count > 20)
    {
        AnsiConsole.MarkupLine($"  [red]... and {accumulator.Errors.Count - 20} more errors[/]");
    }
}
else
{
    AnsiConsole.MarkupLine("[green]✓ All transactions valid[/]");
}
AnsiConsole.WriteLine();

bool allPassed = results.All(r => r.Error is null) && !accumulator.HasErrors;
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

static void DisplaySystemInfo(SystemInfo info)
{
    var panel = new Panel(
        new Rows(
            new Text($"Machine: {info.MachineName}", new Style(Color.Cyan1)),
            new Text($"OS: {info.OSDescription}", new Style(Color.Cyan1)),
            new Text($"Architecture: {info.OSArchitecture}"),
            new Text($"Cores: {info.ProcessorCount}"),
            new Text($"RAM: {FormatBytes(info.TotalMemory)} total, {FormatBytes(info.AvailableMemory)} available"),
            new Text($"Runtime: {info.RuntimeVersion}"),
            new Text(""),
            new Text($"Process ID: {info.ProcessId}", new Style(Color.Cyan1)),
            new Text($"Threads: {info.ThreadCount}"),
            new Text($"Working Set: {FormatBytes(info.WorkingSetBytes)}"),
            new Text($"Started: {info.StartTime:yyyy-MM-dd HH:mm:ss}")
        )
    )
    .Header(new PanelHeader("[cyan bold]System Information[/]"))
    .Border(BoxBorder.Rounded)
    .BorderColor(Color.Cyan1);

    AnsiConsole.Write(panel);
}

static async Task ClearDatabase(IDbContextFactory<LoadTestDbContext> factory)
{
    using var db = factory.CreateDbContext();
    await db.AuditLogs.ExecuteDeleteAsync();
    await db.Transactions.ExecuteDeleteAsync();
    await db.Accounts.ExecuteDeleteAsync();
}

// ─────────────────────────────────────────────────────────────────────────────
// Result record
// ─────────────────────────────────────────────────────────────────────────────

record ScenarioResult(string Label, int Transactions, TimeSpan Elapsed, long Tps, long PeakBytes, long AllocatedBytes, int GcGen0, string? Error);
