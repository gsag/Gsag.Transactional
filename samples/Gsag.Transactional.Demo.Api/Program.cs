using System.Diagnostics;
using System.Reflection;
using Gsag.Transactional.Core.Extensions;
using Gsag.Transactional.Demo.Api.Data;
using Gsag.Transactional.Demo.Api.Infrastructure;
using Gsag.Transactional.Demo.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<CheckoutDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("PostgreSQL")));

// Per-request collectors — Scoped so each HTTP request gets its own list.
// Hooks registered inside [Transactional] methods write to these; the controller
// reads them after the service call returns and includes them in the response.
builder.Services.AddScoped<HookOutputCollector>();
builder.Services.AddScoped<InMemoryEventBus>();
builder.Services.AddScoped<IEventBus>(sp => sp.GetRequiredService<InMemoryEventBus>());

// Configure transactional services: the calling assembly is automatically scanned
// for service classes with [Transactional] methods and I{Name} interface (OrderService,
// InventoryService, PaymentService, AuditService, CheckoutService, InventoryReportService).
// Two observers are registered and the proxy wraps them in CompositeTransactionObserver,
// calling each in registration order.
builder.Services.AddTransactional(b => b
    .AddLogging()                                 // LoggingTransactionObserver (MEL)
    .AddObserver<InMemoryMetricsObserver>());

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new()
    {
        Title = "Transactional Demo — E-Commerce Checkout",
        Version = "v1",
        Description =
            "Eight endpoints, each demonstrating a distinct [Transactional] capability: " +
            "Required, RequiresNew, Suppress, NoRollbackFor, AfterCommit, AfterRollback, AfterCompletion hooks. " +
            "Every response includes HooksOutput and PublishedEvents so you can observe the library's behavior directly in the response body."
    });

    var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
    c.IncludeXmlComments(xmlPath);
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CheckoutDbContext>();
    // EnsureCreated is intentional for this demo — no migrations needed.
    // Note: EnsureCreated and Migrate() are mutually exclusive; enabling migrations
    // later would require dropping and recreating the database.
    db.Database.EnsureCreated();
}

app.UseSwagger(options => options.OpenApiVersion = Microsoft.OpenApi.OpenApiSpecVersion.OpenApi3_1);
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Transactional Demo v1"));

if (app.Environment.IsDevelopment())
{
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        var url = app.Urls.FirstOrDefault(u => u.StartsWith("https://"))
               ?? app.Urls.FirstOrDefault(u => u.StartsWith("http://"));
        if (url is not null)
        {
            Process.Start(new ProcessStartInfo($"{url}/swagger") { UseShellExecute = true });
        }
    });
}

app.MapControllers();
app.Run();

public partial class Program { }
