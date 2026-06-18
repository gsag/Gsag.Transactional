using Microsoft.EntityFrameworkCore;

namespace LoadTest.Data;

class LoadTestAccount
{
    public int Id { get; set; }
    public required string Name { get; init; }
    public decimal Balance { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

class LoadTestTransaction
{
    public int Id { get; set; }
    public int AccountId { get; set; }
    public decimal Amount { get; set; }
    public required string Type { get; init; } // "Debit" or "Credit"
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

class LoadTestAuditLog
{
    public int Id { get; set; }
    public required string Operation { get; init; }
    public required string Data { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

class LoadTestDbContext : DbContext
{
    public LoadTestDbContext(DbContextOptions<LoadTestDbContext> options) : base(options) { }

    public DbSet<LoadTestAccount> Accounts => Set<LoadTestAccount>();
    public DbSet<LoadTestTransaction> Transactions => Set<LoadTestTransaction>();
    public DbSet<LoadTestAuditLog> AuditLogs => Set<LoadTestAuditLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LoadTestAccount>(b =>
        {
            b.Property(a => a.Name).HasMaxLength(100).IsRequired();
            b.HasIndex(a => a.CreatedAt);
        });

        modelBuilder.Entity<LoadTestTransaction>(b =>
        {
            b.Property(t => t.Type).HasMaxLength(10).IsRequired();
            b.HasOne<LoadTestAccount>().WithMany().HasForeignKey(t => t.AccountId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(t => t.AccountId);
            b.HasIndex(t => t.CreatedAt);
        });

        modelBuilder.Entity<LoadTestAuditLog>(b =>
        {
            b.Property(l => l.Operation).HasMaxLength(50).IsRequired();
            b.Property(l => l.Data).HasMaxLength(500).IsRequired();
            b.HasIndex(l => l.Timestamp);
        });
    }
}
