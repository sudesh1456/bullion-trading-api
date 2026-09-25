using BullionTrading.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BullionTrading.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<RateAlert> RateAlerts => Set<RateAlert>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        // Readable enums in the database, and sortable timestamps on SQLite.
        builder.Properties<Enum>().HaveConversion<string>().HaveMaxLength(16);
        builder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
        builder.Properties<decimal>().HavePrecision(18, 2);
    }

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<User>(e =>
        {
            e.HasIndex(u => u.Email).IsUnique();
            e.Property(u => u.Email).HasMaxLength(256);
            e.Property(u => u.DisplayName).HasMaxLength(100);
        });

        model.Entity<Product>(e =>
        {
            e.HasIndex(p => p.Sku).IsUnique();
            e.Property(p => p.Sku).HasMaxLength(32);
            e.Property(p => p.Name).HasMaxLength(100);
            e.Property(p => p.WeightGrams).HasPrecision(18, 3);
        });

        model.Entity<Order>(e =>
        {
            e.Ignore(o => o.Total);
            e.HasIndex(o => o.Status);
            e.HasIndex(o => new { o.UserId, o.CreatedAt });
            e.HasIndex(o => new { o.UserId, o.IdempotencyKey }).IsUnique().HasFilter("IdempotencyKey IS NOT NULL");
            e.Property(o => o.IdempotencyKey).HasMaxLength(64);
        });

        model.Entity<RateAlert>(e => e.HasIndex(a => new { a.Status, a.Metal }));
    }
}
