using System.Data.Common;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Gsag.Transactional.Observability.Extensions;

/// <summary>
/// Extension methods for configuring health checks for the observability stack.
/// </summary>
internal static class HealthCheckExtensions
{
    /// <summary>
    /// Adds health checks for PostgreSQL and the Grafana LGTM stack.
    /// </summary>
    internal static IServiceCollection AddObservabilityHealthChecks(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var connectionString = configuration.GetConnectionString("Database");

        services.AddHealthChecks()
            .AddCheck<PostgreSqlHealthCheck>(
                "postgresql",
                failureStatus: HealthStatus.Unhealthy,
                tags: ["ready"],
                timeout: TimeSpan.FromSeconds(5))
            .AddCheck<GrafanaHealthCheck>(
                "grafana",
                failureStatus: HealthStatus.Unhealthy,
                tags: ["ready"]);

        services.AddSingleton(new PostgreSqlHealthCheck(connectionString ?? string.Empty));

        return services;
    }
}

internal sealed class GrafanaHealthCheck : IHealthCheck
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(3) };
    private static readonly Uri GrafanaUri = new("http://localhost:3000/api/health");

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await HttpClient.GetAsync(GrafanaUri, cancellationToken);
            return response.IsSuccessStatusCode
                ? HealthCheckResult.Healthy("Grafana is reachable.")
                : HealthCheckResult.Unhealthy($"Grafana returned {response.StatusCode}.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Grafana is not reachable.", ex);
        }
    }
}

internal sealed class PostgreSqlHealthCheck(string connectionString) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return HealthCheckResult.Unhealthy("Connection string is not configured.");
        }

        try
        {
            var connectionType = Type.GetType("Npgsql.NpgsqlConnection, Npgsql")
                ?? throw new InvalidOperationException("Npgsql driver not found.");

            using var connection = (DbConnection)Activator.CreateInstance(connectionType, connectionString)!;
            await connection.OpenAsync(cancellationToken);
            return HealthCheckResult.Healthy("PostgreSQL is reachable.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL is not reachable.", ex);
        }
    }
}
