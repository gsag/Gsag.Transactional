using Gsag.Transactional.Demo.Api.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Gsag.Transactional.Demo.Api.Data;

public class CheckoutDbContext : DbContext
{
    public CheckoutDbContext(DbContextOptions<CheckoutDbContext> options) : base(options) { }

    public DbSet<CheckoutOrder> Orders => Set<CheckoutOrder>();
    public DbSet<InventoryReservation> Reservations => Set<InventoryReservation>();
    public DbSet<PaymentRecord> Payments => Set<PaymentRecord>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

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

        // EF Core SQLite cannot translate DateTimeOffset in ORDER BY clauses.
        // Storing as ISO 8601 text lets SQLite sort correctly; all values are UTC so
        // lexicographic order matches chronological order.
        var converter = new DateTimeOffsetToStringConverter();
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties()
                .Where(p => p.ClrType == typeof(DateTimeOffset)))
            {
                property.SetValueConverter(converter);
            }
        }
    }
}
