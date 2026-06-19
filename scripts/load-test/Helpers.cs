using System.Collections.Concurrent;
using LoadTest.Data;
using LoadTest.System;
using LoadTest.Validation;
using Microsoft.EntityFrameworkCore;
using Spectre.Console;

namespace LoadTest.Helpers;

record ScenarioResult(string Label, int Transactions, TimeSpan Elapsed, long Tps, long PeakBytes, long AllocatedBytes, int GcGen0, string? Error);

static class Formatting
{
    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F1} GB",
        >= 1_048_576     => $"{bytes / 1_048_576.0:F1} MB",
        _                => $"{bytes / 1024.0:F0} KB",
    };

    public static void DisplaySystemInfo(LoadTest.System.SystemInfo info)
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

    public static void AssertEq(int actual, int expected, string label)
    {
        if (actual != expected)
        {
            throw new Exception($"{label}: expected {expected}, got {actual}");
        }
    }
}

static class Database
{
    public static async Task ClearDatabase(IDbContextFactory<LoadTestDbContext> factory)
    {
        using var db = factory.CreateDbContext();
        await db.Entities.ExecuteDeleteAsync();
    }
}

sealed class ProgressBar
{
    private long _completed;
    private readonly long _total;
    private readonly ProgressTask _task;

    public ProgressBar(ProgressTask task, long total)
    {
        _total = total;
        _task = task;
    }

    public void Increment()
    {
        long now = Interlocked.Increment(ref _completed);
        _task.Value = Math.Min(100, (now * 100L) / _total);
    }

    public long Completed => Volatile.Read(ref _completed);
}

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
