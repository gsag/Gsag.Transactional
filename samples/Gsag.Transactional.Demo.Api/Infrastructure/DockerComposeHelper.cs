using System.Diagnostics;

namespace Gsag.Transactional.Demo.Api.Infrastructure;

internal static class DockerComposeHelper
{
    private static readonly string ComposeFile = Path.Combine(AppContext.BaseDirectory, "docker-compose.yml");

    internal static async Task EnsurePostgresIsRunningAsync(string connStr)
    {
        const int maxRetries = 30;
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

        throw new InvalidOperationException("PostgreSQL failed to start after 30 seconds");
    }

    internal static async Task StopDockerComposeAsync()
    {
        if (!File.Exists(ComposeFile))
        {
            return;
        }

        try
        {
            // Remove orphans and volumes to ensure complete cleanup
            var psi = CreateProcessInfo("compose down --remove-orphans --volumes");
            using var process = Process.Start(psi);
            if (process is null)
                return;

            await process.WaitForExitAsync();

            if (process.ExitCode == 0)
            {
                Console.WriteLine("✓ PostgreSQL container and volumes stopped and removed");
            }
            else
            {
                Console.WriteLine($"Warning: docker compose down exited with code {process.ExitCode}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Failed to stop docker compose: {ex.Message}");
        }
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
        if (!File.Exists(ComposeFile))
        {
            throw new FileNotFoundException($"docker-compose.yml not found at {ComposeFile}");
        }

        try
        {
            var psi = CreateProcessInfo("compose up -d");
            using var process = Process.Start(psi);
            if (process is null)
                throw new InvalidOperationException("Failed to start docker compose");

            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"docker compose exited with code {process.ExitCode}");
            }

            Console.WriteLine("✓ docker compose started successfully");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to start docker compose. Ensure Docker is installed and running.", ex);
        }
    }

    private static ProcessStartInfo CreateProcessInfo(string arguments)
    {
#pragma warning disable S4036 // PATH variable check; ComposeFile is immutable and derived at initialization
        var composePath = ComposeFile;
#pragma warning restore S4036
        return new()
        {
            FileName = "docker",
            Arguments = $"--file {composePath} {arguments}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
    }
}
