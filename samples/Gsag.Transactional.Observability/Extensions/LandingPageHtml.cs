namespace Gsag.Transactional.Observability.Extensions;

internal static class LandingPageHtml
{
    internal const string Content = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
            <meta charset="UTF-8">
            <meta name="viewport" content="width=device-width, initial-scale=1.0">
            <title>Observability Dashboard</title>
            <style>
                * { margin: 0; padding: 0; box-sizing: border-box; }
                body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; background: #0f172a; color: #e2e8f0; min-height: 100vh; display: flex; flex-direction: column; align-items: center; padding: 3rem 1rem; }
                h1 { font-size: 2rem; font-weight: 700; margin-bottom: 0.5rem; }
                .subtitle { color: #94a3b8; margin-bottom: 2.5rem; font-size: 1.1rem; }
                .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(260px, 1fr)); gap: 1.25rem; max-width: 900px; width: 100%; }
                .card { background: #1e293b; border: 1px solid #334155; border-radius: 12px; padding: 1.5rem; text-decoration: none; color: inherit; transition: transform 0.15s, border-color 0.15s, box-shadow 0.15s; display: flex; flex-direction: column; gap: 0.5rem; }
                .card:hover { transform: translateY(-2px); border-color: #3b82f6; box-shadow: 0 4px 20px rgba(59,130,246,0.15); }
                .card-icon { font-size: 1.8rem; }
                .card-title { font-size: 1.1rem; font-weight: 600; }
                .card-desc { color: #94a3b8; font-size: 0.875rem; line-height: 1.4; }
                .card-url { color: #3b82f6; font-size: 0.75rem; word-break: break-all; }
                .badge { display: inline-block; padding: 0.15rem 0.5rem; border-radius: 6px; font-size: 0.7rem; font-weight: 600; text-transform: uppercase; }
                .badge-app { background: #1d4ed8; }
                .badge-stack { background: #7c3aed; }
                .badge-health { background: #059669; }
            </style>
        </head>
        <body>
            <h1>Observability Dashboard</h1>
            <p class="subtitle">Gsag.Transactional &mdash; OpenTelemetry Demo</p>
            <div class="grid">
                <a class="card" href="/swagger">
                    <span class="card-icon">&#x1F4D6;</span>
                    <span class="card-title">Swagger UI <span class="badge badge-app">API</span></span>
                    <span class="card-desc">Explore and test all transactional endpoints.</span>
                    <span class="card-url">/swagger</span>
                </a>
                <a class="card" href="http://localhost:3000" target="_blank">
                    <span class="card-icon">&#x1F4CA;</span>
                    <span class="card-title">Grafana <span class="badge badge-stack">Traces &middot; Metrics &middot; Logs</span></span>
                    <span class="card-desc">Unified observability: traces, metrics and logs via LGTM stack.</span>
                    <span class="card-url">http://localhost:3000</span>
                </a>
                <a class="card" href="/health/ready">
                    <span class="card-icon">&#x2705;</span>
                    <span class="card-title">Health &mdash; Ready <span class="badge badge-health">Readiness</span></span>
                    <span class="card-desc">Checks PostgreSQL and Grafana dependency status.</span>
                    <span class="card-url">/health/ready</span>
                </a>
                <a class="card" href="/health/live">
                    <span class="card-icon">&#x1F3D8;</span>
                    <span class="card-title">Health &mdash; Live <span class="badge badge-health">Liveness</span></span>
                    <span class="card-desc">Confirms the application process is running.</span>
                    <span class="card-url">/health/live</span>
                </a>
            </div>
        </body>
        </html>
        """;
}
