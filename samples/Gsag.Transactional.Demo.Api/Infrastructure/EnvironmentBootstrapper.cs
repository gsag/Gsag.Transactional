using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Gsag.Transactional.Demo.Api.Infrastructure;

internal class EnvironmentBootstrapper
{
    private static readonly HttpClient HealthClient = new()
    {
        Timeout = TimeSpan.FromSeconds(2)
    };

    private static readonly Uri GrafanaHealthUri = new("http://localhost:3000/api/health");

    private readonly ILogger<EnvironmentBootstrapper> _logger;
    private readonly int _maxRetries;
    private readonly int _delayMs;
    private static readonly string ContainerFile = Path.Combine(AppContext.BaseDirectory, "docker-compose.yml");

    public EnvironmentBootstrapper(ILogger<EnvironmentBootstrapper> logger, int maxRetries = 30, int delayMs = 1000)
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
                _logger.LogInformation("Database is ready");
                await EnsureObservabilityStackIsReadyAsync();
                return;
            }

            if (i == 0)
            {
                _logger.LogInformation("Database not accessible, starting container...");
                await StartContainerAsync();
            }

            await Task.Delay(_delayMs);
        }

        var totalSeconds = _maxRetries * _delayMs / 1000;
        _logger.LogError("Database failed to start after {TotalSeconds} seconds", totalSeconds);
        throw new InvalidOperationException($"Database failed to start after {totalSeconds} seconds. Ensure Docker is running and docker-compose.yml is accessible.");
    }

    private async Task EnsureObservabilityStackIsReadyAsync()
    {
        for (int i = 0; i < _maxRetries; i++)
        {
            if (await IsObservabilityStackReadyAsync())
            {
                _logger.LogInformation("Observability stack is ready");
                return;
            }

            await Task.Delay(_delayMs);
        }

        var totalSeconds = _maxRetries * _delayMs / 1000;
        _logger.LogError("Observability stack failed to become ready after {TotalSeconds} seconds", totalSeconds);
        throw new InvalidOperationException($"Observability stack failed to become ready after {totalSeconds} seconds. Check the Grafana LGTM container.");
    }

    internal async Task StopContainerAsync()
    {
        await RunContainerCommandAsync(
            "down --remove-orphans --volumes",
            successMessage: "Container and volumes stopped and removed",
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

    private static async Task<bool> IsObservabilityStackReadyAsync()
    {
        return await IsEndpointReadyAsync(GrafanaHealthUri);
    }

    private static async Task<bool> IsEndpointReadyAsync(Uri endpoint)
    {
        try
        {
            using var response = await HealthClient.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task StartContainerAsync()
    {
        _logger.LogInformation("Starting container with docker-compose file at: {ContainerPath}", ContainerFile);
        await RunContainerCommandAsync(
            "up -d",
            successMessage: "Container started successfully",
            errorMessage: "container startup");
    }

    private async Task RunContainerCommandAsync(string commandArgs, string successMessage, string errorMessage)
    {
        if (!File.Exists(ContainerFile))
        {
            _logger.LogError("docker-compose.yml not found at {ContainerPath}. Expected location: {BaseDirectory}", ContainerFile, AppContext.BaseDirectory);
            throw new FileNotFoundException($"docker-compose.yml not found at {ContainerFile}");
        }

        try
        {
            _logger.LogDebug("Executing: docker compose -f {ContainerFile} {CommandArgs}", ContainerFile, commandArgs);
            var psi = CreateContainerProcessInfo(commandArgs);
            using var process = Process.Start(psi);
            if (process is null)
            {
                _logger.LogError("Failed to start {ErrorMessage} - Process.Start returned null. " +
                    "Verify 'docker' command is available in PATH", errorMessage);
                throw new InvalidOperationException($"Failed to start {errorMessage} - cannot create process. " +
                    "Ensure Docker is installed and 'docker' command is in your PATH.");
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await Task.WhenAll(outputTask, errorTask);
            _ = await outputTask;
            var error = await errorTask;

            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                var errorDetail = string.IsNullOrWhiteSpace(error) ? string.Empty : $"\nDocker stderr: {error}";
                _logger.LogError("{ErrorMessage} exited with code {ExitCode}{ErrorDetail}",
                    errorMessage, process.ExitCode, errorDetail);
                throw new InvalidOperationException($"{errorMessage} exited with code {process.ExitCode}. " +
                    $"Check Docker is running and available.{errorDetail}");
            }

            _logger.LogInformation(successMessage);
        }
        catch (Exception ex) when (ex.GetType().Name == "Win32Exception" && ex.Message.Contains("docker"))
        {
            _logger.LogError(ex, "Docker command not found in PATH: {Message}. Install Docker Desktop and ensure " +
                "the 'docker' command is available in your system PATH.", ex.Message);
            throw new InvalidOperationException(
                "Docker is not installed or not in PATH. Please install Docker Desktop and ensure it's in your system PATH.",
                ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to run {ErrorMessage}.", errorMessage);
            throw new InvalidOperationException($"Failed to run {errorMessage}. {ex.Message}", ex);
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
