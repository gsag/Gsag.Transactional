using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Gsag.Transactional.Observability.Extensions;

internal sealed class ObservabilityStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.Use(async (context, next) =>
            {
                var path = context.Request.Path.Value;

                if (path == "/")
                {
                    context.Response.ContentType = "text/html; charset=utf-8";
                    await context.Response.WriteAsync(LandingPageHtml.Content);
                    return;
                }

                if (path == "/health/ready")
                {
                    await HandleHealthCheckAsync(context, tags: ["ready"]);
                    return;
                }

                if (path == "/health/live")
                {
                    await HandleHealthCheckAsync(context, tags: []);
                    return;
                }

                await next();
            });

            next(app);
        };
    }

    private static async Task HandleHealthCheckAsync(HttpContext context, string[] tags)
    {
        var healthCheckService = context.RequestServices.GetRequiredService<HealthCheckService>();

        var report = await healthCheckService.CheckHealthAsync(
            predicate: tags.Length == 0 ? null : check => check.Tags.Contains(tags[0]),
            cancellationToken: context.RequestAborted);

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
