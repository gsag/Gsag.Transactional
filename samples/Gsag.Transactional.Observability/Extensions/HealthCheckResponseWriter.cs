using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Gsag.Transactional.Observability.Extensions;

internal static class HealthCheckResponseWriter
{
    internal static async Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";

        var result = new Dictionary<string, object>
        {
            ["status"] = report.Status.ToString(),
            ["totalDuration"] = report.TotalDuration.TotalMilliseconds,
            ["checks"] = report.Entries.Select(e => new Dictionary<string, object>
            {
                ["name"] = e.Key,
                ["status"] = e.Value.Status.ToString(),
                ["duration"] = e.Value.Duration.TotalMilliseconds,
                ["description"] = e.Value.Description ?? string.Empty,
                ["exception"] = e.Value.Exception?.Message ?? string.Empty
            }).ToList()
        };

        await context.Response.WriteAsync(
            JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }
}
