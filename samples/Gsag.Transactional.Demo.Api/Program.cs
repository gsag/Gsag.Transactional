using System.Reflection;
using Gsag.Transactional.Core.Extensions;
using Gsag.Transactional.Demo.Api.Data;
using Gsag.Transactional.Demo.Api.Infrastructure;
using Gsag.Transactional.Observability.Extensions;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Database");

builder.Services.AddSingleton<EnvironmentBootstrapper>();
builder.Services.AddDbContext<CheckoutDbContext>(options => options.UseNpgsql(connectionString));

// Per-request collectors — Scoped so each HTTP request gets its own list.
// Hooks registered inside [Transactional] methods write to these; the controller
// reads them after the service call returns and includes them in the response.
builder.Services.AddScoped<HookOutputCollector>();
builder.Services.AddScoped<InMemoryEventBus>();
builder.Services.AddScoped<IEventBus>(sp => sp.GetRequiredService<InMemoryEventBus>());

builder.Services.AddObservabilityPipeline(builder.Configuration);

// Configure transactional services: the calling assembly is automatically scanned
// for service classes with [Transactional] methods and I{Name} interface (OrderService,
// InventoryService, PaymentService, AuditService, CheckoutService, InventoryReportService).
// Three observers are registered and the proxy wraps them in CompositeTransactionObserver,
// calling each in registration order.
builder.Services.AddTransactional(b => b
    .AddLogging()                                 // LoggingTransactionObserver (MEL)
    .AddObservability()
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

app.UseSwagger(options => options.OpenApiVersion = Microsoft.OpenApi.OpenApiSpecVersion.OpenApi3_1);
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Transactional Demo v1"));

app.MapControllers();
app.MapObservabilityEndpoints();

// Initialize database before running
app.Lifetime.ApplicationStarted.Register(async () =>
{
    using var scope = app.Services.CreateScope();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    try
    {
        var bootstrapper = scope.ServiceProvider.GetRequiredService<EnvironmentBootstrapper>();
        await bootstrapper.EnsureDatabaseIsReadyAsync(connectionString!);

        var db = scope.ServiceProvider.GetRequiredService<CheckoutDbContext>();
        await db.Database.EnsureCreatedAsync();
        logger.LogInformation("Database schema ensured");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to initialize application");
        throw new InvalidOperationException("Application initialization failed. Ensure Docker is running, docker-compose.yml exists, and database connection is properly configured.", ex);
    }
});

// Stop container on shutdown
app.Lifetime.ApplicationStopping.Register(async () =>
{
    using var scope = app.Services.CreateScope();
    var bootstrapper = scope.ServiceProvider.GetRequiredService<EnvironmentBootstrapper>();
    await bootstrapper.StopContainerAsync();
});

await app.RunAsync();

public partial class Program { }
