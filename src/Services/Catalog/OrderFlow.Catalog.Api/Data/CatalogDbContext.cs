using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using OrderFlow.Catalog.Api.Domain;

namespace OrderFlow.Catalog.Api.Data;

public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Reservation> Reservations => Set<Reservation>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema("catalog");

        b.Entity<Product>(e =>
        {
            e.HasKey(p => p.Sku);
            e.Property(p => p.Sku).HasMaxLength(40);
            e.Property(p => p.Name).HasMaxLength(200);
            e.Property(p => p.Price).HasPrecision(18, 2);
            e.Property(p => p.Version).IsConcurrencyToken();
            e.Ignore(p => p.Available);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_Products_OnHand", "[OnHand] >= 0");
                t.HasCheckConstraint("CK_Products_Reserved", "[Reserved] >= 0 AND [Reserved] <= [OnHand]");
            });
        });

        b.Entity<Reservation>(e =>
        {
            e.HasKey(r => r.OrderId);
            e.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(r => r.Reason).HasMaxLength(500);
            e.OwnsMany(r => r.Lines, l =>
            {
                l.ToTable("ReservationLines");
                l.WithOwner().HasForeignKey("OrderId");
                l.Property<int>("Id");
                l.HasKey("Id");
                l.Property(x => x.Sku).HasMaxLength(40);
            });
        });

        // MassTransit transactional outbox + inbox tables.
        b.AddInboxStateEntity();
        b.AddOutboxMessageEntity();
        b.AddOutboxStateEntity();
    }
}

/// <summary>Used only by `dotnet ef` to build migrations; never connects anywhere.</summary>
public sealed class CatalogDbContextFactory : IDesignTimeDbContextFactory<CatalogDbContext>
{
    public CatalogDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<CatalogDbContext>().UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=design-time-only").Options);
}
