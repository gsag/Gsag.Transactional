using System.Diagnostics;
using LoadTest.Data;
using LoadTest.Helpers;
using LoadTest.Observers;
using LoadTest.Services;
using LoadTest.Validation;
using Microsoft.EntityFrameworkCore;
using Spectre.Console;

namespace LoadTest.Scenarios;

static class TestScenarios
{
    public static async Task<ScenarioResult> RunPureThroughputAsync(
        int scenarioNum, int totalScenarios,
        int throughputTasks, int throughputIterationsPerTask,
        ILoadService load, ConcurrencyObserver obs, LifecycleAccumulator acc,
        IDbContextFactory<LoadTestDbContext> dbFactory)
    {
        obs.Reset();
        long peak = 0; long alloc = 0; int gc0 = 0;

        var result = await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("cyan"))
            .StartAsync($"[cyan]{scenarioNum}/{totalScenarios}[/]  Pure throughput...", async _ =>
            {
                await Database.ClearDatabase(dbFactory);
                using var sampler = new PeakMemorySampler();
                long allocBefore = GC.GetTotalAllocatedBytes();
                int gcBefore = GC.CollectionCount(0);
                var sw = Stopwatch.StartNew();
                var completed = 0L;
                var logInterval = Math.Max(1, throughputTasks / 10);
                var tasks = Enumerable.Range(0, throughputTasks)
                    .Select(_ => Task.Run(async () =>
                    {
                        for (int i = 0; i < throughputIterationsPerTask; i++)
                        {
                            await load.InsertAsync();
                            long done = Interlocked.Increment(ref completed);
                            if (done % logInterval == 0)
                                AnsiConsole.MarkupLine($"[dim]{done:N0} / {throughputTasks * throughputIterationsPerTask:N0}[/]");
                        }
                    }));
                await Task.WhenAll(tasks);
                sw.Stop();
                peak = sampler.PeakBytes;
                alloc = GC.GetTotalAllocatedBytes() - allocBefore;
                gc0 = GC.CollectionCount(0) - gcBefore;
                return sw.Elapsed;
            });

        int total = throughputTasks * throughputIterationsPerTask;
        long tps = (long)(total / result.TotalSeconds);
        string? error = null;
        try
        {
            Formatting.AssertEq(obs.Commit, total, "Commit");
            Formatting.AssertEq(obs.Rollback, 0, "Rollback");
            Formatting.AssertEq(obs.Complete, total, "Complete");
        }
        catch (Exception ex) { error = ex.Message; }
        acc.CaptureErrors(1, obs.ValidateConsistency());
        return new($"Pure throughput ({throughputTasks}×{throughputIterationsPerTask})", total, result, tps, peak, alloc, gc0, error);
    }

    public static async Task<ScenarioResult> RunRollbackAsync(
        int scenarioNum, int totalScenarios,
        int rollbackTasks,
        ILoadService load, ConcurrencyObserver obs, LifecycleAccumulator acc,
        IDbContextFactory<LoadTestDbContext> dbFactory)
    {
        obs.Reset();
        long peak = 0; long alloc = 0; int gc0 = 0;

        var result = await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("cyan"))
            .StartAsync($"[cyan]{scenarioNum}/{totalScenarios}[/]  Rollback vs commit...", async _ =>
            {
                await Database.ClearDatabase(dbFactory);
                int half = rollbackTasks / 2;
                using var sampler = new PeakMemorySampler();
                long allocBefore = GC.GetTotalAllocatedBytes();
                int gcBefore = GC.CollectionCount(0);
                var sw = Stopwatch.StartNew();
                var completed = 0L;
                var logInterval = Math.Max(1, rollbackTasks / 10);
                var tasks = Enumerable.Range(0, rollbackTasks).Select(i =>
                {
                    if (i < half)
                    {
                        return Task.Run(async () =>
                        {
                            await load.InsertAsync();
                            long done = Interlocked.Increment(ref completed);
                            if (done % logInterval == 0)
                                AnsiConsole.MarkupLine($"[dim]{done:N0} / {rollbackTasks:N0}[/]");
                        });
                    }

                    return Task.Run(async () =>
                    {
                        try { await load.InsertFailAsync(); }
                        catch (InvalidOperationException) { }
                        long done = Interlocked.Increment(ref completed);
                        if (done % logInterval == 0)
                            AnsiConsole.MarkupLine($"[dim]{done:N0} / {rollbackTasks:N0}[/]");
                    });
                });
                await Task.WhenAll(tasks);
                sw.Stop();
                peak = sampler.PeakBytes;
                alloc = GC.GetTotalAllocatedBytes() - allocBefore;
                gc0 = GC.CollectionCount(0) - gcBefore;
                return sw.Elapsed;
            });

        int half2 = rollbackTasks / 2;
        long tps = (long)(rollbackTasks / result.TotalSeconds);
        string? error = null;
        try
        {
            Formatting.AssertEq(obs.Commit, half2, "Commit");
            Formatting.AssertEq(obs.Rollback, half2, "Rollback");
            Formatting.AssertEq(obs.Complete, rollbackTasks, "Complete");
        }
        catch (Exception ex) { error = ex.Message; }
        acc.CaptureErrors(2, obs.ValidateConsistency());
        return new($"Rollback vs commit ({rollbackTasks} tasks)", rollbackTasks, result, tps, peak, alloc, gc0, error);
    }

    public static async Task<ScenarioResult> RunAsyncLocalIsolationAsync(
        int scenarioNum, int totalScenarios,
        int isolationTasks,
        IIsolationService isolation, ConcurrencyObserver obs, LifecycleAccumulator acc,
        IDbContextFactory<LoadTestDbContext> dbFactory)
    {
        obs.Reset();
        var hookFireCount = new int[isolationTasks];
        long peak = 0; long alloc = 0; int gc0 = 0;

        var result = await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("cyan"))
            .StartAsync($"[cyan]{scenarioNum}/{totalScenarios}[/]  AsyncLocal isolation...", async _ =>
            {
                await Database.ClearDatabase(dbFactory);
                using var sampler = new PeakMemorySampler();
                long allocBefore = GC.GetTotalAllocatedBytes();
                int gcBefore = GC.CollectionCount(0);
                var sw = Stopwatch.StartNew();
                var completed = 0L;
                var logInterval = Math.Max(1, isolationTasks / 10);
                var tasks = Enumerable.Range(0, isolationTasks)
                    .Select(i => Task.Run(async () =>
                    {
                        await isolation.UpdateAsync(i, () => Interlocked.Increment(ref hookFireCount[i]));
                        long done = Interlocked.Increment(ref completed);
                        if (done % logInterval == 0)
                            AnsiConsole.MarkupLine($"[dim]{done:N0} / {isolationTasks:N0}[/]");
                    }));
                await Task.WhenAll(tasks);
                sw.Stop();
                peak = sampler.PeakBytes;
                alloc = GC.GetTotalAllocatedBytes() - allocBefore;
                gc0 = GC.CollectionCount(0) - gcBefore;
                return sw.Elapsed;
            });

        long tps = (long)(isolationTasks / result.TotalSeconds);
        string? error = null;
        try
        {
            for (int i = 0; i < isolationTasks; i++)
            {
                if (hookFireCount[i] != 1)
                {
                    throw new Exception($"Task {i}: hook fired {hookFireCount[i]}x (expected 1) — AsyncLocal leaked");
                }
            }
        }
        catch (Exception ex) { error = ex.Message; }
        acc.CaptureErrors(3, obs.ValidateConsistency());
        return new($"AsyncLocal isolation ({isolationTasks} tasks)", isolationTasks, result, tps, peak, alloc, gc0, error);
    }

    public static async Task<ScenarioResult> RunNestedRequiresNewAsync(
        int scenarioNum, int totalScenarios,
        int nestedTasks,
        IOuterService outer, ConcurrencyObserver obs, LifecycleAccumulator acc,
        IDbContextFactory<LoadTestDbContext> dbFactory)
    {
        obs.Reset();
        long peak = 0; long alloc = 0; int gc0 = 0;

        var result = await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("cyan"))
            .StartAsync($"[cyan]{scenarioNum}/{totalScenarios}[/]  Nested RequiresNew...", async _ =>
            {
                await Database.ClearDatabase(dbFactory);
                using var sampler = new PeakMemorySampler();
                long allocBefore = GC.GetTotalAllocatedBytes();
                int gcBefore = GC.CollectionCount(0);
                var sw = Stopwatch.StartNew();
                var completed = 0L;
                var logInterval = Math.Max(1, nestedTasks / 10);
                var tasks = Enumerable.Range(0, nestedTasks)
                    .Select(_ => Task.Run(async () =>
                    {
                        await outer.RunWithInnerAsync();
                        long done = Interlocked.Increment(ref completed);
                        if (done % logInterval == 0)
                            AnsiConsole.MarkupLine($"[dim]{done:N0} / {nestedTasks:N0}[/]");
                    }));
                await Task.WhenAll(tasks);
                sw.Stop();
                peak = sampler.PeakBytes;
                alloc = GC.GetTotalAllocatedBytes() - allocBefore;
                gc0 = GC.CollectionCount(0) - gcBefore;
                return sw.Elapsed;
            });

        int totalScopes = nestedTasks * 2;
        long tps = (long)(totalScopes / result.TotalSeconds);
        string? error = null;
        try
        {
            Formatting.AssertEq(obs.Begin, totalScopes, "Begin (outer + inner)");
            Formatting.AssertEq(obs.Commit, totalScopes, "Commit (outer + inner)");
            Formatting.AssertEq(obs.Complete, totalScopes, "Complete (outer + inner)");
        }
        catch (Exception ex) { error = ex.Message; }
        acc.CaptureErrors(4, obs.ValidateConsistency());
        return new($"Nested RequiresNew ({nestedTasks} tasks)", totalScopes, result, tps, peak, alloc, gc0, error);
    }

    public static async Task<ScenarioResult> RunNestedWithFailureAsync(
        int scenarioNum, int totalScenarios,
        int nestedWithFailureTasks,
        INestedFailureService nestedFailure, ConcurrencyObserver obs, LifecycleAccumulator acc,
        IDbContextFactory<LoadTestDbContext> dbFactory)
    {
        obs.Reset();
        long peak = 0; long alloc = 0; int gc0 = 0;

        var result = await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("cyan"))
            .StartAsync($"[cyan]{scenarioNum}/{totalScenarios}[/]  Nested RequiresNew (inner fails)...", async _ =>
            {
                await Database.ClearDatabase(dbFactory);
                using var sampler = new PeakMemorySampler();
                long allocBefore = GC.GetTotalAllocatedBytes();
                int gcBefore = GC.CollectionCount(0);
                var sw = Stopwatch.StartNew();
                var tasks = Enumerable.Range(0, nestedWithFailureTasks)
                    .Select(_ => Task.Run(() => nestedFailure.RunOuterWithFailingInnerAsync()));
                await Task.WhenAll(tasks);
                sw.Stop();
                peak = sampler.PeakBytes;
                alloc = GC.GetTotalAllocatedBytes() - allocBefore;
                gc0 = GC.CollectionCount(0) - gcBefore;
                return sw.Elapsed;
            });

        int totalScopes = nestedWithFailureTasks * 2;
        long tps = (long)(totalScopes / result.TotalSeconds);
        string? error = null;
        try
        {
            Formatting.AssertEq(obs.Begin, totalScopes, "Begin (outer + inner)");
            Formatting.AssertEq(obs.Commit, nestedWithFailureTasks, "Commit (outer commits, inner fails)");
            Formatting.AssertEq(obs.Rollback, nestedWithFailureTasks, "Rollback (inner fails)");
            Formatting.AssertEq(obs.Complete, totalScopes, "Complete (outer + inner)");
        }
        catch (Exception ex) { error = ex.Message; }
        acc.CaptureErrors(5, obs.ValidateConsistency());
        return new($"Nested RequiresNew with failure ({nestedWithFailureTasks} tasks)", totalScopes, result, tps, peak, alloc, gc0, error);
    }

    public static async Task<ScenarioResult> RunExceptionHandlingAsync(
        int scenarioNum, int totalScenarios,
        int exceptionTasks,
        IExceptionService exception, ConcurrencyObserver obs, LifecycleAccumulator acc,
        IDbContextFactory<LoadTestDbContext> dbFactory)
    {
        obs.Reset();
        long peak = 0; long alloc = 0; int gc0 = 0;

        var result = await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("cyan"))
            .StartAsync($"[cyan]{scenarioNum}/{totalScenarios}[/]  Exception handling...", async _ =>
            {
                await Database.ClearDatabase(dbFactory);
                int third = exceptionTasks / 3;
                using var sampler = new PeakMemorySampler();
                long allocBefore = GC.GetTotalAllocatedBytes();
                int gcBefore = GC.CollectionCount(0);
                var sw = Stopwatch.StartNew();
                var completed = 0L;
                var logInterval = Math.Max(1, exceptionTasks / 10);
                var tasks = Enumerable.Range(0, exceptionTasks).Select(i =>
                {
                    if (i < third)
                    {
                        return Task.Run(async () =>
                        {
                            try { await exception.ThrowDuringExecutionAsync(); }
                            catch { }
                        });
                    }
                    else if (i < 2 * third)
                    {
                        return Task.Run(async () =>
                        {
                            try { await exception.ThrowInHookAsync(); }
                            catch { }
                        });
                    }
                    else
                    {
                        return Task.Run(async () =>
                        {
                            try { await exception.ThrowCustomExceptionAsync(); }
                            catch { }
                        });
                    }
                });
                await Task.WhenAll(tasks);
                sw.Stop();
                peak = sampler.PeakBytes;
                alloc = GC.GetTotalAllocatedBytes() - allocBefore;
                gc0 = GC.CollectionCount(0) - gcBefore;
                return sw.Elapsed;
            });

        int third2 = exceptionTasks / 3;
        long tps = (long)(exceptionTasks / result.TotalSeconds);
        string? error = null;
        try
        {
            Formatting.AssertEq(obs.Begin, exceptionTasks, "Begin");
            Formatting.AssertEq(obs.Commit, third2, "Commit (exceptions in hooks after commit)");
            Formatting.AssertEq(obs.Rollback, 2 * third2, "Rollback (exceptions during execution)");
            Formatting.AssertEq(obs.Complete, exceptionTasks, "Complete");
        }
        catch (Exception ex) { error = ex.Message; }
        acc.CaptureErrors(6, obs.ValidateConsistency());
        return new($"Exception handling ({exceptionTasks} tasks)", exceptionTasks, result, tps, peak, alloc, gc0, error);
    }

    public static async Task<ScenarioResult> RunExceptionPropagationAsync(
        int scenarioNum, int totalScenarios,
        int exceptionPropagationTasks,
        IExceptionPropagationService propagation, ConcurrencyObserver obs, LifecycleAccumulator acc,
        IDbContextFactory<LoadTestDbContext> dbFactory)
    {
        obs.Reset();
        var rollbackObserverFired = new int[exceptionPropagationTasks];
        long peak = 0; long alloc = 0; int gc0 = 0;

        var result = await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("cyan"))
            .StartAsync($"[cyan]{scenarioNum}/{totalScenarios}[/]  Exception propagation...", async _ =>
            {
                await Database.ClearDatabase(dbFactory);
                using var sampler = new PeakMemorySampler();
                long allocBefore = GC.GetTotalAllocatedBytes();
                int gcBefore = GC.CollectionCount(0);
                var sw = Stopwatch.StartNew();
                var tasks = Enumerable.Range(0, exceptionPropagationTasks)
                    .Select(i => Task.Run(async () =>
                    {
                        bool exceptionCaught = false;
                        try
                        {
                            await propagation.ThrowAndVerifyPropagationAsync(i, rollbackObserverFired);
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
                peak = sampler.PeakBytes;
                alloc = GC.GetTotalAllocatedBytes() - allocBefore;
                gc0 = GC.CollectionCount(0) - gcBefore;
                return sw.Elapsed;
            });

        long tps = (long)(exceptionPropagationTasks / result.TotalSeconds);
        string? error = null;
        try
        {
            Formatting.AssertEq(obs.Begin, exceptionPropagationTasks, "Begin");
            Formatting.AssertEq(obs.Rollback, exceptionPropagationTasks, "Rollback");
            Formatting.AssertEq(obs.Complete, exceptionPropagationTasks, "Complete");
            int unfiredObservers = rollbackObserverFired.Count(fired => fired == 0);
            if (unfiredObservers > 0)
                throw new Exception($"Rollback observers did not fire: {unfiredObservers} tasks");
        }
        catch (Exception ex) { error = ex.Message; }
        acc.CaptureErrors(7, obs.ValidateConsistency());
        return new($"Exception propagation ({exceptionPropagationTasks} tasks)", exceptionPropagationTasks, result, tps, peak, alloc, gc0, error);
    }

    public static async Task<ScenarioResult> RunIOSimulationAsync(
        int scenarioNum, int totalScenarios,
        int ioSimulationTasks,
        IIOSimulationService ioSim, ConcurrencyObserver obs, LifecycleAccumulator acc,
        IDbContextFactory<LoadTestDbContext> dbFactory)
    {
        obs.Reset();
        long peak = 0; long alloc = 0; int gc0 = 0;

        var result = await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("cyan"))
            .StartAsync($"[cyan]{scenarioNum}/{totalScenarios}[/]  I/O simulation...", async _ =>
            {
                await Database.ClearDatabase(dbFactory);
                using var sampler = new PeakMemorySampler();
                long allocBefore = GC.GetTotalAllocatedBytes();
                int gcBefore = GC.CollectionCount(0);
                var sw = Stopwatch.StartNew();
                var tasks = Enumerable.Range(0, ioSimulationTasks)
                    .Select(_ => Task.Run(() => ioSim.SimulateIOAsync()));
                await Task.WhenAll(tasks);
                sw.Stop();
                peak = sampler.PeakBytes;
                alloc = GC.GetTotalAllocatedBytes() - allocBefore;
                gc0 = GC.CollectionCount(0) - gcBefore;
                return sw.Elapsed;
            });

        long tps = (long)(ioSimulationTasks / result.TotalSeconds);
        string? error = null;
        try
        {
            Formatting.AssertEq(obs.Begin, ioSimulationTasks, "Begin");
            Formatting.AssertEq(obs.Commit, ioSimulationTasks, "Commit");
            Formatting.AssertEq(obs.Complete, ioSimulationTasks, "Complete");
        }
        catch (Exception ex) { error = ex.Message; }
        acc.CaptureErrors(8, obs.ValidateConsistency());
        return new($"I/O simulation ({ioSimulationTasks} tasks)", ioSimulationTasks, result, tps, peak, alloc, gc0, error);
    }

    public static async Task<ScenarioResult> RunHookOrderingAsync(
        int scenarioNum, int totalScenarios,
        int hookOrderingTasks,
        IHookOrderingService hookOrdering, ConcurrencyObserver obs, LifecycleAccumulator acc,
        IDbContextFactory<LoadTestDbContext> dbFactory)
    {
        obs.Reset();
        var hookOrderingFire = new int[hookOrderingTasks * 3];
        long peak = 0; long alloc = 0; int gc0 = 0;

        var result = await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("cyan"))
            .StartAsync($"[cyan]{scenarioNum}/{totalScenarios}[/]  Hook ordering...", async _ =>
            {
                await Database.ClearDatabase(dbFactory);
                using var sampler = new PeakMemorySampler();
                long allocBefore = GC.GetTotalAllocatedBytes();
                int gcBefore = GC.CollectionCount(0);
                var sw = Stopwatch.StartNew();
                var tasks = Enumerable.Range(0, hookOrderingTasks)
                    .Select(i => Task.Run(() => hookOrdering.ValidateHookOrderAsync(i, hookOrderingFire)));
                await Task.WhenAll(tasks);
                sw.Stop();
                peak = sampler.PeakBytes;
                alloc = GC.GetTotalAllocatedBytes() - allocBefore;
                gc0 = GC.CollectionCount(0) - gcBefore;
                return sw.Elapsed;
            });

        long tps = (long)(hookOrderingTasks / result.TotalSeconds);
        string? error = null;
        try
        {
            Formatting.AssertEq(obs.Begin, hookOrderingTasks, "Begin");
            Formatting.AssertEq(obs.Commit, hookOrderingTasks, "Commit");
            Formatting.AssertEq(obs.Complete, hookOrderingTasks, "Complete");
            for (int i = 0; i < hookOrderingFire.Length; i++)
            {
                if (hookOrderingFire[i] != 1)
                    throw new Exception($"Hook {i}: fired {hookOrderingFire[i]}x (expected 1)");
            }
        }
        catch (Exception ex) { error = ex.Message; }
        acc.CaptureErrors(9, obs.ValidateConsistency());
        return new($"Hook ordering ({hookOrderingTasks} tasks)", hookOrderingTasks, result, tps, peak, alloc, gc0, error);
    }
}
