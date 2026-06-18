using Microsoft.EntityFrameworkCore;

namespace LoadTest.Data;

class Entity
{
    public int Id { get; set; }
    public int Value { get; set; }
}

class LoadTestDbContext : DbContext
{
    public LoadTestDbContext(DbContextOptions<LoadTestDbContext> options) : base(options) { }

    public DbSet<Entity> Entities => Set<Entity>();
}
