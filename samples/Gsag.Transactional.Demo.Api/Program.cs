using System.Diagnostics;
using System.Reflection;
using Gsag.Transactional.Core.Extensions;
using Gsag.Transactional.Demo.Api.Data;
using Gsag.Transactional.Demo.Api.Infrastructure;
using Gsag.Transactional.Demo.Api.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

await DockerComposeHelper.EnsurePostgresIsRunningAsync(builder.Configuration.GetConnectionString("PostgreSQL")!);

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

    app.Lifetime.ApplicationStopping.Register(async () =>
    {
        Console.WriteLine("\nShutting down PostgreSQL container...");
        await DockerComposeHelper.StopDockerComposeAsync();
    });
}

app.MapControllers();
app.Run();

// Helper for docker compose initialization and cleanup
file static class DockerComposeHelper
{
    private static readonly string ComposeFile = Path.Combine(AppContext.BaseDirectory, "docker-compose.yml");

    internal static async Task EnsurePostgresIsRunningAsync(string connStr)
    {
        const int maxRetries = 30;
        const int delayMs = 1000;

        for (int i = 0; i < maxRetries; i++)
        {
            if (await IsPostgresAccessibleAsync(connStr))
            {
                Console.WriteLine("✓ PostgreSQL is ready");
                return;
            }

            if (i == 0)
            {
                Console.WriteLine("PostgreSQL not accessible, attempting to start docker-compose...");
                await StartDockerComposeAsync();
            }
            await Task.Delay(delayMs);
        }

        throw new InvalidOperationException("PostgreSQL failed to start after 30 seconds");
    }

    internal static async Task StopDockerComposeAsync()
    {
        if (!File.Exists(ComposeFile))
        {
            return;
        }

        try
        {
            // Remove orphans and volumes to ensure complete cleanup
            var psi = CreateProcessInfo("compose down --remove-orphans --volumes");
            using var process = Process.Start(psi);
            if (process is null)
                return;

            await process.WaitForExitAsync();

            if (process.ExitCode == 0)
            {
                Console.WriteLine("✓ PostgreSQL container and volumes stopped and removed");
            }
            else
            {
                Console.WriteLine($"Warning: docker compose down exited with code {process.ExitCode}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Failed to stop docker compose: {ex.Message}");
        }
    }

    private static async Task<bool> IsPostgresAccessibleAsync(string connStr)
    {
        try
        {
            using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync();
            await conn.CloseAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task StartDockerComposeAsync()
    {
        if (!File.Exists(ComposeFile))
        {
            throw new FileNotFoundException($"docker-compose.yml not found at {ComposeFile}");
        }

        try
        {
            var psi = CreateProcessInfo("compose up -d");
            using var process = Process.Start(psi);
            if (process is null)
                throw new InvalidOperationException("Failed to start docker compose");

            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"docker compose exited with code {process.ExitCode}");
            }

            Console.WriteLine("✓ docker compose started successfully");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to start docker compose. Ensure Docker is installed and running.", ex);
        }
    }

    private static ProcessStartInfo CreateProcessInfo(string arguments) =>
        new()
        {
            FileName = "docker",
            Arguments = arguments,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
}

public partial class Program { }
