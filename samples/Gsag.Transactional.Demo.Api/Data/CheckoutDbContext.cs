using System.Diagnostics;
using Gsag.Transactional.Demo.Api.Entities;
using Gsag.Transactional.Observability;
using Microsoft.EntityFrameworkCore;

namespace Gsag.Transactional.Demo.Api.Data;

public class CheckoutDbContext : DbContext
{
    private static readonly ActivitySource ActivitySource = new(OpenTelemetryConventions.InstrumentationName);

    public CheckoutDbContext(DbContextOptions<CheckoutDbContext> options) : base(options)
    {
    }

    public DbSet<CheckoutOrder> Orders => Set<CheckoutOrder>();
    public DbSet<InventoryReservation> Reservations => Set<InventoryReservation>();
    public DbSet<PaymentRecord> Payments => Set<PaymentRecord>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    public override int SaveChanges()
    {
        return ExecuteSaveChanges(() => base.SaveChanges());
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        return ExecuteSaveChanges(() => base.SaveChanges(acceptAllChangesOnSuccess));
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        return ExecuteSaveChangesAsync(() => base.SaveChangesAsync(cancellationToken));
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        return ExecuteSaveChangesAsync(() => base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken));
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CheckoutOrder>(b =>
        {
            b.Property(o => o.Scenario).HasMaxLength(100).IsRequired();
            b.Property(o => o.Status).HasMaxLength(50).IsRequired();
            b.HasIndex(o => o.CreatedAt);
        });

        modelBuilder.Entity<InventoryReservation>(b =>
        {
            b.Property(r => r.ProductId).HasMaxLength(50).IsRequired();
            b.HasOne<CheckoutOrder>().WithMany().HasForeignKey(r => r.OrderId);
            b.HasIndex(r => r.OrderId);
        });

        modelBuilder.Entity<PaymentRecord>(b =>
        {
            b.Property(p => p.Status).HasMaxLength(50).IsRequired();
            b.HasOne<CheckoutOrder>().WithMany().HasForeignKey(p => p.OrderId);
            b.HasIndex(p => p.OrderId);
        });

        modelBuilder.Entity<AuditEntry>(b =>
        {
            b.Property(e => e.Action).HasMaxLength(100).IsRequired();
            b.Property(e => e.Scenario).HasMaxLength(100).IsRequired();
            b.HasIndex(e => e.OccurredAt);
        });
    }

    private int ExecuteSaveChanges(Func<int> saveChanges)
    {
        using var activity = StartSaveChangesActivity();

        try
        {
            var affectedRows = saveChanges();
            SetSuccessTags(activity, affectedRows);
            return affectedRows;
        }
        catch (Exception exception)
        {
            SetFailureTags(activity, exception);
            throw;
        }
    }

    private async Task<int> ExecuteSaveChangesAsync(Func<Task<int>> saveChangesAsync)
    {
        using var activity = StartSaveChangesActivity();

        try
        {
            var affectedRows = await saveChangesAsync();
            SetSuccessTags(activity, affectedRows);
            return affectedRows;
        }
        catch (Exception exception)
        {
            SetFailureTags(activity, exception);
            throw;
        }
    }

    private Activity? StartSaveChangesActivity()
    {
        var activity = ActivitySource.StartActivity(OpenTelemetryConventions.Database.SaveChangesActivity, ActivityKind.Client);
        if (activity is null)
        {
            return null;
        }

        var pendingEntries = ChangeTracker.Entries().Count(entry => entry.State is not EntityState.Unchanged and not EntityState.Detached);
        activity.SetTag(OpenTelemetryConventions.Database.DbSystem, OpenTelemetryConventions.Database.PostgresSystem);
        activity.SetTag(OpenTelemetryConventions.Database.Operation, OpenTelemetryConventions.Database.SaveChangesOperation);
        activity.SetTag(OpenTelemetryConventions.Database.AffectedEntries, pendingEntries);
        return activity;
    }

    private static void SetSuccessTags(Activity? activity, int affectedRows)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetTag(OpenTelemetryConventions.Database.AffectedRows, affectedRows);
        activity.SetStatus(ActivityStatusCode.Ok);
    }

    private static void SetFailureTags(Activity? activity, Exception exception)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetTag(OpenTelemetryConventions.Tags.ExceptionType, exception.GetType().FullName);
        activity.SetTag(OpenTelemetryConventions.Tags.ExceptionMessage, exception.Message);
        activity.SetStatus(ActivityStatusCode.Error, exception.Message);
    }
}
