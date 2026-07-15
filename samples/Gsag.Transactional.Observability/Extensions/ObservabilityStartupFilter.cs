using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Gsag.Transactional.Observability.Extensions;

internal sealed class ObservabilityStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapObservabilityHealthChecks();
                endpoints.MapObservabilityDashboard();
            });

            next(app);
        };
    }
}
