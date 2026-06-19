using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LoadTest.System;

record SystemInfo
{
    // Machine Information
    public required string MachineName { get; init; }
    public required string OSDescription { get; init; }
    public required string OSArchitecture { get; init; }
    public required int ProcessorCount { get; init; }
    public required long TotalMemory { get; init; }
    public required long AvailableMemory { get; init; }
    public required string RuntimeVersion { get; init; }

    // Process Information
    public required int ProcessId { get; init; }
    public required int ThreadCount { get; init; }
    public required long WorkingSetBytes { get; init; }
    public required DateTime StartTime { get; init; }
}

static class SystemInfoCollector
{
    public static SystemInfo Collect()
    {
        var proc = Process.GetCurrentProcess();
        var gcInfo = GC.GetGCMemoryInfo();
        var totalSystemMemory = GC.GetGCMemoryInfo().HeapSizeBytes + gcInfo.TotalAvailableMemoryBytes;

        return new SystemInfo
        {
            MachineName = Environment.MachineName,
            OSDescription = RuntimeInformation.OSDescription,
            OSArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            ProcessorCount = Environment.ProcessorCount,
            TotalMemory = totalSystemMemory,
            AvailableMemory = gcInfo.TotalAvailableMemoryBytes,
            RuntimeVersion = RuntimeInformation.FrameworkDescription,
            ProcessId = proc.Id,
            ThreadCount = proc.Threads.Count,
            WorkingSetBytes = proc.WorkingSet64,
            StartTime = proc.StartTime
        };
    }
}
