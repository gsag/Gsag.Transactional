using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Gsag.Transactional.Demo.Api.Infrastructure;

internal static class DockerComposeHelper
{
    private static readonly string ComposeFile = Path.Combine(AppContext.BaseDirectory, "docker-compose.yml");

    internal static async Task EnsurePostgresIsRunningAsync(string connStr)
    {
        const int maxRetries = 10;
        const int delayMs = 1000;

        for (int i = 0; i < maxRetries; i++)
        {
            if (await IsPostgresAccessibleAsync(connStr))
            {
                Console.WriteLine("✓ PostgreSQL is ready");
                return;
            }

            if (i == 0)
            {
                Console.WriteLine("PostgreSQL not accessible, attempting to start docker-compose...");
                await StartDockerComposeAsync();
            }
            await Task.Delay(delayMs);
        }

        throw new InvalidOperationException("PostgreSQL failed to start after 10 seconds");
    }

    internal static async Task StopDockerComposeAsync()
    {
        await RunDockerComposeCommandAsync(
            "compose down --remove-orphans --volumes",
            successMessage: "✓ PostgreSQL container and volumes stopped and removed",
            errorMessage: "docker compose down");
    }

    private static async Task<bool> IsPostgresAccessibleAsync(string connStr)
    {
        try
        {
            using var conn = new Npgsql.NpgsqlConnection(connStr);
            await conn.OpenAsync();
            await conn.CloseAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task StartDockerComposeAsync()
    {
        await RunDockerComposeCommandAsync(
            "compose up -d",
            successMessage: "✓ docker compose started successfully",
            errorMessage: "docker compose");
    }

    private static async Task RunDockerComposeCommandAsync(string commandArgs, string successMessage, string errorMessage)
    {
        if (!File.Exists(ComposeFile))
        {
            throw new FileNotFoundException($"docker-compose.yml not found at {ComposeFile}");
        }

        try
        {
            var psi = CreateProcessInfo(commandArgs);
            using var process = Process.Start(psi);
            if (process is null)
            {
                throw new InvalidOperationException($"Failed to start {errorMessage}");
            }

            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"{errorMessage} exited with code {process.ExitCode}");
            }

            Console.WriteLine(successMessage);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to run {errorMessage}. Ensure Docker is installed and running.", ex);
        }
    }

    [SuppressMessage("Security", "S4036", Justification = "composeFilePath is output-relative and immutable; docker-compose.yml is copied to output dir by build")]
    private static ProcessStartInfo CreateProcessInfo(string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "docker",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("--file");
        psi.ArgumentList.Add(ComposeFile);

        foreach (var arg in arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            psi.ArgumentList.Add(arg);
        }

        return psi;
    }
}
