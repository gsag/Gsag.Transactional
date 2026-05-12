using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Gsag.Transactional.Core.Extensions;
using Gsag.Transactional.Core.Observability;
using Gsag.Transactional.Demo.Api.Data;
using Gsag.Transactional.Demo.Api.Infrastructure;
using Gsag.Transactional.Demo.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// Absolute path prevents working-directory ambiguity between `dotnet run`,
// published binaries, and test hosts.
var dbPath = Path.Combine(AppContext.BaseDirectory, "checkout.db");
builder.Services.AddDbContext<CheckoutDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}")
           // SQLite does not support System.Transactions ambient enlistment.
           // The proxy lifecycle (scope commit/rollback, hooks, observer) works correctly;
           // the database simply ignores the ambient scope. Suppress the warning so it
           // doesn't surface as an unhandled exception in the demo API.
           .ConfigureWarnings(w => w.Ignore(RelationalEventId.AmbientTransactionWarning)));

// Per-request collectors — Scoped so each HTTP request gets its own list.
// Hooks registered inside [Transactional] methods write to these; the controller
// reads them after the service call returns and includes them in the response.
builder.Services.AddScoped<HookOutputCollector>();
builder.Services.AddScoped<InMemoryEventBus>();
builder.Services.AddScoped<IEventBus>(sp => sp.GetRequiredService<InMemoryEventBus>());

// Two observers registered side-by-side — the proxy wraps them in CompositeTransactionObserver
// and calls each in registration order. Composite Observer pattern in action.
builder.Services.AddTransactionalLogging()                                 // LoggingTransactionObserver (MEL)
                .AddTransactionalObserver<InMemoryMetricsObserver>();      // InMemoryMetricsObserver (counters)

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

app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Transactional Demo v1"));

app.MapControllers();
app.Run();

public partial class Program { }
