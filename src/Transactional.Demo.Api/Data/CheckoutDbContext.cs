using Microsoft.EntityFrameworkCore;
using Transactional.Demo.Api.Entities;

namespace Transactional.Demo.Api.Data;

public class CheckoutDbContext : DbContext
{
    public CheckoutDbContext(DbContextOptions<CheckoutDbContext> options) : base(options) { }

    public DbSet<CheckoutOrder> Orders => Set<CheckoutOrder>();
    public DbSet<InventoryReservation> Reservations => Set<InventoryReservation>();
    public DbSet<PaymentRecord> Payments => Set<PaymentRecord>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
}
