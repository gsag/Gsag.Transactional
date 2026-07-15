using System.Diagnostics;
using System.Reflection;

namespace Gsag.Transactional.Observability;

internal sealed record ObservabilityServiceMetadata(string ServiceName, string? ServiceVersion);

internal static class ObservabilityServiceMetadataResolver
{
    private static readonly Lazy<string> CachedGitPath = new(ResolveGitPath);
    internal static ObservabilityServiceMetadata Resolve(ObservabilityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var entryAssembly = Assembly.GetEntryAssembly();
        return new ObservabilityServiceMetadata(
            ResolveServiceName(options, entryAssembly),
            ResolveServiceVersion(options, entryAssembly));
    }

    private static string ResolveServiceName(ObservabilityOptions options, Assembly? entryAssembly) =>
        FirstNonEmpty(options.ServiceName, entryAssembly?.GetName().Name)
        ?? OpenTelemetryConventions.InstrumentationName;

    private static string? ResolveServiceVersion(ObservabilityOptions options, Assembly? entryAssembly) =>
        FirstNonEmpty(
            options.ServiceVersion,
            ResolveGitTagVersion(),
            ResolveAssemblyVersion(entryAssembly));

    private static string? ResolveAssemblyVersion(Assembly? entryAssembly)
    {
        var informationalVersion = entryAssembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return IsDefaultGeneratedVersion(informationalVersion) ? null : informationalVersion;
    }

    private static string? ResolveGitTagVersion()
    {
        var repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);
        if (repositoryRoot is null)
        {
            return null;
        }

        try
        {
            var processInfo = new ProcessStartInfo
            {
                FileName = CachedGitPath.Value,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            processInfo.ArgumentList.Add("-C");
            processInfo.ArgumentList.Add(repositoryRoot);
            processInfo.ArgumentList.Add("tag");
            processInfo.ArgumentList.Add("--list");
            processInfo.ArgumentList.Add("--sort=-version:refname");

            using var process = Process.Start(processInfo);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(2000);
            if (process.ExitCode != 0)
            {
                return null;
            }

            return output
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static string? FindRepositoryRoot(string baseDirectory)
    {
        var current = new DirectoryInfo(baseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git")) || File.Exists(Path.Combine(current.FullName, ".git")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }

    private static string ResolveGitPath()
    {
        var command = Environment.OSVersion.Platform == PlatformID.Win32NT ? "where" : "which";

        var process = Process.Start(new ProcessStartInfo
        {
            FileName = command,
            ArgumentList = { "git" },
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        });

        var output = process?.StandardOutput.ReadToEnd().Trim();
        process?.WaitForExit(2000);

        if (!string.IsNullOrWhiteSpace(output))
        {
            return output.Split('\n')[0].Trim();
        }

        return "git";
    }

    private static bool IsDefaultGeneratedVersion(string? version) =>
        string.IsNullOrWhiteSpace(version) || version == "1.0.0";

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
