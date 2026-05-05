using Microsoft.EntityFrameworkCore;
using Transactional.Core.Extensions;
using Transactional.Core.Observability;
using Transactional.Demo.Api.Data;
using Transactional.Demo.Api.Infrastructure;
using Transactional.Demo.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// Absolute path prevents working-directory ambiguity between `dotnet run`,
// published binaries, and test hosts.
var dbPath = Path.Combine(AppContext.BaseDirectory, "checkout.db");
builder.Services.AddDbContext<CheckoutDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"));

// Per-request collectors — Scoped so each HTTP request gets its own list.
// Hooks registered inside [Transactional] methods write to these; the controller
// reads them after the service call returns and includes them in the response.
builder.Services.AddScoped<HookOutputCollector>();
builder.Services.AddScoped<InMemoryEventBus>();
builder.Services.AddScoped<IEventBus>(sp => sp.GetRequiredService<InMemoryEventBus>());

// Emit structured DEBUG/WARNING log entries for every transaction lifecycle event.
builder.Services.AddTransactionalLogging();

// Discover all service classes with [Transactional] methods and I{Name} interface:
// OrderService, InventoryService, PaymentService, AuditService,
// CheckoutService, InventoryReportService.
builder.Services.AddTransactionalServices(typeof(CheckoutService).Assembly);

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

app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Transactional Demo v1"));

app.MapControllers();
app.Run();

public partial class Program { }
