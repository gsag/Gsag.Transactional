using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Gsag.Transactional.Demo.Api.Infrastructure;

internal class EnvironmentBootstrapper
{
    private readonly ILogger<EnvironmentBootstrapper> _logger;
    private readonly int _maxRetries;
    private readonly int _delayMs;
    private static readonly string ContainerFile = Path.Combine(AppContext.BaseDirectory, "docker-compose.yml");

    public EnvironmentBootstrapper(ILogger<EnvironmentBootstrapper> logger, int maxRetries = 10, int delayMs = 1000)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _maxRetries = maxRetries;
        _delayMs = delayMs;
    }

    internal async Task EnsureDatabaseIsReadyAsync(string connectionString)
    {
        for (int i = 0; i < _maxRetries; i++)
        {
            if (await IsDatabaseAccessibleAsync(connectionString))
            {
                _logger.LogInformation("✓ Database is ready");
                return;
            }

            if (i == 0)
            {
                _logger.LogInformation("Database not accessible, attempting to start container...");
                await StartContainerAsync();
            }
            await Task.Delay(_delayMs);
        }

        var totalSeconds = _maxRetries * _delayMs / 1000;
        throw new InvalidOperationException($"Database failed to start after {totalSeconds} seconds");
    }

    internal async Task StopContainerAsync()
    {
        await RunContainerCommandAsync(
            "down --remove-orphans --volumes",
            successMessage: "✓ Container and volumes stopped and removed",
            errorMessage: "container shutdown");
    }

    private async Task<bool> IsDatabaseAccessibleAsync(string connectionString)
    {
        try
        {
            using var conn = new Npgsql.NpgsqlConnection(connectionString);
            await conn.OpenAsync();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Database connection attempt failed");
            return false;
        }
    }

    private async Task StartContainerAsync()
    {
        await RunContainerCommandAsync(
            "up -d",
            successMessage: "✓ Container started successfully",
            errorMessage: "container startup");
    }

    private async Task RunContainerCommandAsync(string commandArgs, string successMessage, string errorMessage)
    {
        if (!File.Exists(ContainerFile))
        {
            _logger.LogError("docker-compose.yml not found at {ContainerPath}", ContainerFile);
            throw new FileNotFoundException($"docker-compose.yml not found at {ContainerFile}");
        }

        try
        {
            var psi = CreateContainerProcessInfo(commandArgs);
            using var process = Process.Start(psi);
            if (process is null)
            {
                _logger.LogError("Failed to start {ErrorMessage}", errorMessage);
                throw new InvalidOperationException($"Failed to start {errorMessage}");
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await Task.WhenAll(outputTask, errorTask);
            var output = await outputTask;
            var error = await errorTask;

            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                var errorDetail = string.IsNullOrWhiteSpace(error) ? string.Empty : $"\n{error}";
                _logger.LogError("{ErrorMessage} exited with code {ExitCode}{ErrorDetail}",
                    errorMessage, process.ExitCode, errorDetail);
                throw new InvalidOperationException($"{errorMessage} exited with code {process.ExitCode}{errorDetail}");
            }

            _logger.LogInformation(successMessage);
        }
        catch (FileNotFoundException)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to run {ErrorMessage}. Ensure Docker is installed and running.", errorMessage);
            throw new InvalidOperationException($"Failed to run {errorMessage}. Ensure Docker is installed and running. {ex.Message}", ex);
        }
    }

    [SuppressMessage("Security", "S4036", Justification = "containerFile is output-relative and immutable; docker-compose.yml is copied to output dir by build")]
    private ProcessStartInfo CreateContainerProcessInfo(string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "docker",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("compose");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add(ContainerFile);

        foreach (var arg in arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            psi.ArgumentList.Add(arg);
        }

        return psi;
    }
}
