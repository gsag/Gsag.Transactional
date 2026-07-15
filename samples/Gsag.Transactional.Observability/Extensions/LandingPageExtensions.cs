using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Gsag.Transactional.Observability.Extensions;

internal static class LandingPageExtensions
{
    internal static IEndpointRouteBuilder MapObservabilityDashboard(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/", () => Results.Content("<html><body><h1>Loading...</h1></body></html>", "text/html"));
        return endpoints;
    }
}
