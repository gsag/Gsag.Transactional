using System.Diagnostics;
using System.Transactions;
using Gsag.Transactional.Core.Attributes;
using Gsag.Transactional.Core.Extensions;
using Gsag.Transactional.Core.Hooks;
using Gsag.Transactional.Core.Observability;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

// ─── Configuration ────────────────────────────────────────────────────────────

const int ThroughputTasks = 1_000;
const int ThroughputIterationsPerTask = 5;
const int RollbackTasks = 1_000;        // 500 commit + 500 rollback
const int IsolationTasks = 500;
const int NestedTasks = 200;

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
AnsiConsole.MarkupLine($"[dim]throughput={ThroughputTasks}×{ThroughputIterationsPerTask} | rollback={RollbackTasks} | isolation={IsolationTasks} | nested={NestedTasks}[/]");
AnsiConsole.WriteLine();

var results = new List<ScenarioResult>();
var sw = new Stopwatch();

// ─── Cenário 1: Throughput puro ───────────────────────────────────────────────

observer.Reset();

await AnsiConsole.Status()
    .Spinner(Spinner.Known.Dots)
    .SpinnerStyle(Style.Parse("cyan"))
    .StartAsync("[cyan]1/4[/]  Throughput puro...", async _ =>
    {
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
    results.Add(new($"Throughput puro ({ThroughputTasks}×{ThroughputIterationsPerTask})", total, sw.Elapsed, tps, error));
}

// ─── Cenário 2: Rollback vs commit sob carga ─────────────────────────────────

observer.Reset();

await AnsiConsole.Status()
    .Spinner(Spinner.Known.Dots)
    .SpinnerStyle(Style.Parse("cyan"))
    .StartAsync("[cyan]2/4[/]  Rollback vs commit...", async _ =>
    {
        int half = RollbackTasks / 2;
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
    results.Add(new($"Rollback vs commit ({RollbackTasks} tasks)", RollbackTasks, sw.Elapsed, tps, error));
}

// ─── Cenário 3: Isolamento de hooks (AsyncLocal) ─────────────────────────────

observer.Reset();
var hookFireCount = new int[IsolationTasks];

await AnsiConsole.Status()
    .Spinner(Spinner.Known.Dots)
    .SpinnerStyle(Style.Parse("cyan"))
    .StartAsync("[cyan]3/4[/]  Isolamento AsyncLocal...", async _ =>
    {
        sw.Restart();
        var tasks = Enumerable.Range(0, IsolationTasks)
            .Select(i => Task.Run(async () =>
            {
                await isolationProxy.RunAsync(i, () => Interlocked.Increment(ref hookFireCount[i]));
            }));
        await Task.WhenAll(tasks);
        sw.Stop();
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
    results.Add(new($"Isolamento AsyncLocal ({IsolationTasks} tasks)", IsolationTasks, sw.Elapsed, tps, error));
}

// ─── Cenário 4: Nested RequiresNew concorrente ────────────────────────────────

observer.Reset();

await AnsiConsole.Status()
    .Spinner(Spinner.Known.Dots)
    .SpinnerStyle(Style.Parse("cyan"))
    .StartAsync("[cyan]4/4[/]  Nested RequiresNew...", async _ =>
    {
        sw.Restart();
        var tasks = Enumerable.Range(0, NestedTasks)
            .Select(_ => Task.Run(() => outerProxy.RunWithInnerAsync()));
        await Task.WhenAll(tasks);
        sw.Stop();
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
    results.Add(new($"Nested RequiresNew ({NestedTasks} tasks)", totalScopes, sw.Elapsed, tps, error));
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
    .AddColumn(new TableColumn("[cyan]Status[/]").Centered());

foreach (var r in results)
{
    bool ok = r.Error is null;
    string status = ok ? "[green]✓[/]" : "[red]✗[/]";
    string label = ok ? r.Label : $"{r.Label}\n[red dim]{Markup.Escape(r.Error!)}[/]";
    table.AddRow(label, $"{r.Transactions:N0}", $"{r.Elapsed.TotalMilliseconds:F0} ms", $"{r.Tps:N0}", status);
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

// ─────────────────────────────────────────────────────────────────────────────
// Result record
// ─────────────────────────────────────────────────────────────────────────────

record ScenarioResult(string Label, int Transactions, TimeSpan Elapsed, long Tps, string? Error);

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
