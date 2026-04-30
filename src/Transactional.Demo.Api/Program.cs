using Microsoft.EntityFrameworkCore;
using Transactional.Core.Extensions;
using Transactional.Demo.Api.Data;
using Transactional.Demo.Api.Services;
using Transactional.Core.Observability;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite("Data Source=orders.db"));

// Emit structured DEBUG/WARNING log entries for every transaction lifecycle event.
builder.Services.AddTransactionalLogging();

// Scan the API assembly, find service classes with [Transactional] methods,
// and register each one wrapped in a TransactionProxy.
builder.Services.AddTransactionalServices(typeof(OrderService).Assembly);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "Transactional Demo API", Version = "v1" });
});

var app = builder.Build();

// Ensure the SQLite database and schema exist on first run.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Transactional Demo v1"));

app.MapControllers();
app.Run();

// Exposed for WebApplicationFactory in integration tests.
public partial class Program { }
