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
const int RollbackTasks = 40_000;       // 20 000 commit + 20 000 rollback
const int IsolationTasks = 20_000;
const int NestedTasks = 10_000;

// ─── DI Setup ─────────────────────────────────────────────────────────────────

var observer = new ConcurrencyObserver();

var services = new ServiceCollection();
services.AddSingleton<ITransactionObserver>(observer);
services
    .AddTransactionalService<ILoadService, LoadService>()
    .AddTransactionalService<IInnerService, InnerService>()
    .AddTransactionalService<IOuterService, OuterService>()
    .AddTransactionalService<IIsolationService, IsolationService>();

var sp = services.BuildServiceProvider();
using var scope = sp.CreateScope();
var svcp = scope.ServiceProvider;

ILoadService loadProxy = svcp.GetRequiredService<ILoadService>();
IOuterService outerProxy = svcp.GetRequiredService<IOuterService>();
IIsolationService isolationProxy = svcp.GetRequiredService<IIsolationService>();

// ─── Header ───────────────────────────────────────────────────────────────────

AnsiConsole.Write(new FigletText("Load Test").Color(Color.Cyan1));
AnsiConsole.WriteLine();
AnsiConsole.MarkupLine("[dim]Gsag.Transactional — concurrency & load stress[/]");
AnsiConsole.MarkupLine($"[dim]throughput={ThroughputTasks:N0}×{ThroughputIterationsPerTask} | rollback={RollbackTasks:N0} | isolation={IsolationTasks:N0} | nested={NestedTasks:N0}[/]");
AnsiConsole.WriteLine();

var results = new List<ScenarioResult>();
var sw = new Stopwatch();

// ─── Cenário 1: Throughput puro ───────────────────────────────────────────────

observer.Reset();
long s1Peak = 0; long s1Alloc = 0; int s1Gc0 = 0;

await AnsiConsole.Status()
    .Spinner(Spinner.Known.Dots)
    .SpinnerStyle(Style.Parse("cyan"))
    .StartAsync("[cyan]1/4[/]  Throughput puro...", async _ =>
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
    results.Add(new($"Throughput puro ({ThroughputTasks}×{ThroughputIterationsPerTask})", total, sw.Elapsed, tps, s1Peak, s1Alloc, s1Gc0, error));
}

// ─── Cenário 2: Rollback vs commit sob carga ─────────────────────────────────

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

// ─── Cenário 3: Isolamento de hooks (AsyncLocal) ─────────────────────────────

observer.Reset();
var hookFireCount = new int[IsolationTasks];
long s3Peak = 0; long s3Alloc = 0; int s3Gc0 = 0;

await AnsiConsole.Status()
    .Spinner(Spinner.Known.Dots)
    .SpinnerStyle(Style.Parse("cyan"))
    .StartAsync("[cyan]3/4[/]  Isolamento AsyncLocal...", async _ =>
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
    results.Add(new($"Isolamento AsyncLocal ({IsolationTasks} tasks)", IsolationTasks, sw.Elapsed, tps, s3Peak, s3Alloc, s3Gc0, error));
}

// ─── Cenário 4: Nested RequiresNew concorrente ────────────────────────────────

observer.Reset();
long s4Peak = 0; long s4Alloc = 0; int s4Gc0 = 0;

await AnsiConsole.Status()
    .Spinner(Spinner.Known.Dots)
    .SpinnerStyle(Style.Parse("cyan"))
    .StartAsync("[cyan]4/4[/]  Nested RequiresNew...", async _ =>
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

// ─── Tabela de resultados ─────────────────────────────────────────────────────

AnsiConsole.WriteLine();

var table = new Table()
    .Border(TableBorder.Rounded)
    .BorderColor(Color.Cyan1)
    .AddColumn(new TableColumn("[cyan]Cenário[/]"))
    .AddColumn(new TableColumn("[cyan]Transações[/]").RightAligned())
    .AddColumn(new TableColumn("[cyan]Duração[/]").RightAligned())
    .AddColumn(new TableColumn("[cyan]TPS[/]").RightAligned())
    .AddColumn(new TableColumn("[cyan]Avg µs/tx[/]").RightAligned())
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
        $"{r.Tps:N0}", $"{avgUs:F1}", peak, alloc, $"{r.GcGen0}", status);
}

AnsiConsole.Write(table);
AnsiConsole.WriteLine();

bool allPassed = results.All(r => r.Error is null);
if (allPassed)
{
    AnsiConsole.MarkupLine("[green bold]Todos os cenários passaram.[/]");
}
else
{
    AnsiConsole.MarkupLine("[red bold]Um ou mais cenários falharam.[/]");
    Environment.Exit(1);
}

// ─── Helpers ──────────────────────────────────────────────────────────────────

static void AssertEq(int actual, int expected, string label)
{
    if (actual != expected)
    {
        throw new Exception($"{label}: esperado {expected}, obtido {actual}");
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

// ─────────────────────────────────────────────────────────────────────────────
// Observer
// ─────────────────────────────────────────────────────────────────────────────

sealed class ConcurrencyObserver : ITransactionObserver
{
    private int _begin, _commit, _rollback, _complete;

    public int Begin => _begin;
    public int Commit => _commit;
    public int Rollback => _rollback;
    public int Complete => _complete;

    public void Reset() => _begin = _commit = _rollback = _complete = 0;

    public void OnBegin(TransactionInfo info) => Interlocked.Increment(ref _begin);
    public void OnCommit(TransactionInfo info, TimeSpan elapsed) => Interlocked.Increment(ref _commit);
    public void OnRollback(TransactionInfo info, Exception exception, TimeSpan elapsed) => Interlocked.Increment(ref _rollback);
    public void OnComplete(TransactionInfo info, bool committed, TimeSpan elapsed) => Interlocked.Increment(ref _complete);
}
