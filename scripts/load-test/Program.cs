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
using LoadTest.Scenarios;
using LoadTest.Services;
using LoadTest.System;
using LoadTest.Validation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

// ─── Configuration ────────────────────────────────────────────────────────────

const int TotalScenarios = 9;
const int ThroughputTasks = 1_000;      // 20k total (1k × 20)
const int ThroughputIterationsPerTask = 20;
const int RollbackTasks = 2_000;        // 2k tasks (1k commit + 1k rollback)
const int IsolationTasks = 1_000;
const int NestedTasks = 150;
const int NestedWithFailureTasks = 150;
const int ExceptionTasks = 600;
const int ExceptionPropagationTasks = 500;
const int ISimulationTasks = 400;
const int HookOrderingTasks = 400;

// ─── System Information ────────────────────────────────────────────────────────

var systemInfo = SystemInfoCollector.Collect();

// ─── DI Setup ─────────────────────────────────────────────────────────────────

var observer = new ConcurrencyObserver();
var accumulator = new LifecycleAccumulator();
var dbThrottle = new SemaphoreSlim(100); // Max 100 concurrent DB operations

var services = new ServiceCollection();
services.AddSingleton<ITransactionObserver>(observer);
services.AddSingleton(dbThrottle);
services.AddDbContextFactory<LoadTestDbContext>(options => options.UseNpgsql("Host=localhost;Port=5432;Database=loadtest;Username=loadtest;Password=loadtest123;MaxPoolSize=250;Pooling=true;"));
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

Formatting.DisplaySystemInfo(systemInfo);
AnsiConsole.WriteLine();

var results = new List<ScenarioResult>();

// ─── Run all scenarios ─────────────────────────────────────────────────────────

results.Add(await TestScenarios.RunPureThroughputAsync(
    1, TotalScenarios, ThroughputTasks, ThroughputIterationsPerTask,
    loadProxy, observer, accumulator, dbFactory));

results.Add(await TestScenarios.RunRollbackAsync(
    2, TotalScenarios, RollbackTasks,
    loadProxy, observer, accumulator, dbFactory));

results.Add(await TestScenarios.RunAsyncLocalIsolationAsync(
    3, TotalScenarios, IsolationTasks,
    isolationProxy, observer, accumulator, dbFactory));

results.Add(await TestScenarios.RunNestedRequiresNewAsync(
    4, TotalScenarios, NestedTasks,
    outerProxy, observer, accumulator, dbFactory));

results.Add(await TestScenarios.RunNestedWithFailureAsync(
    5, TotalScenarios, NestedWithFailureTasks,
    nestedFailureProxy, observer, accumulator, dbFactory));

results.Add(await TestScenarios.RunExceptionHandlingAsync(
    6, TotalScenarios, ExceptionTasks,
    exceptionProxy, observer, accumulator, dbFactory));

results.Add(await TestScenarios.RunExceptionPropagationAsync(
    7, TotalScenarios, ExceptionPropagationTasks,
    propagationProxy, observer, accumulator, dbFactory));

results.Add(await TestScenarios.RunIOSimulationAsync(
    8, TotalScenarios, ISimulationTasks,
    ioProxy, observer, accumulator, dbFactory));

results.Add(await TestScenarios.RunHookOrderingAsync(
    9, TotalScenarios, HookOrderingTasks,
    hookOrderingProxy, observer, accumulator, dbFactory));

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
    string peak = Formatting.FormatBytes(r.PeakBytes);
    string alloc = Formatting.FormatBytes(r.AllocatedBytes);
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

