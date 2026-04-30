using Microsoft.EntityFrameworkCore;
using Transactional.Demo.Api.Entities;

namespace Transactional.Demo.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Order> Orders => Set<Order>();
}
