using MassTransit;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OrderFlow.Orders.Api.Sagas;

namespace OrderFlow.Orders.Api.Data;

/// <summary>An accepted order: who bought what at which (server-side) price. Immutable after creation;
/// its progress lives in the saga's <see cref="OrderState"/>.</summary>
public sealed class Order
{
    private Order() { }

    public Order(Guid id, string customerEmail, IEnumerable<OrderItem> items, string? idempotencyKey)
    {
        Id = id;
        CustomerEmail = customerEmail;
        Items = items.ToList();
        Total = Items.Sum(i => i.Quantity * i.UnitPrice);
        IdempotencyKey = idempotencyKey;
    }

    public Guid Id { get; private set; }
    public string CustomerEmail { get; private set; } = null!;
    public decimal Total { get; private set; }
    public string? IdempotencyKey { get; private set; }
    public DateTime CreatedAtUtc { get; private set; } = DateTime.UtcNow;
    public List<OrderItem> Items { get; private set; } = [];
}

public sealed record OrderItem(string Sku, string Name, int Quantity, decimal UnitPrice);

public sealed class OrdersDbContext(DbContextOptions<OrdersDbContext> options) : SagaDbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderState> OrderStates => Set<OrderState>();

    protected override IEnumerable<ISagaClassMap> Configurations => [new OrderStateMap()];

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema("orders");
        base.OnModelCreating(b);

        b.Entity<Order>(e =>
        {
            e.HasKey(o => o.Id);
            e.Property(o => o.Id).ValueGeneratedNever();
            e.Property(o => o.CustomerEmail).HasMaxLength(256);
            e.Property(o => o.Total).HasPrecision(18, 2);
            e.Property(o => o.IdempotencyKey).HasMaxLength(100);
            e.HasIndex(o => o.IdempotencyKey).IsUnique().HasFilter("[IdempotencyKey] IS NOT NULL");
            e.HasIndex(o => new { o.CustomerEmail, o.CreatedAtUtc });
            e.OwnsMany(o => o.Items, i =>
            {
                i.ToTable("OrderItems");
                i.WithOwner().HasForeignKey("OrderId");
                i.Property<int>("Id");
                i.HasKey("Id");
                i.Property(x => x.Sku).HasMaxLength(40);
                i.Property(x => x.Name).HasMaxLength(200);
                i.Property(x => x.UnitPrice).HasPrecision(18, 2);
            });
        });

        b.AddInboxStateEntity();
        b.AddOutboxMessageEntity();
        b.AddOutboxStateEntity();
    }
}

public sealed class OrderStateMap : SagaClassMap<OrderState>
{
    protected override void Configure(EntityTypeBuilder<OrderState> e, ModelBuilder model)
    {
        e.ToTable("OrderStates");
        e.Property(x => x.CurrentState).HasMaxLength(64);
        e.Property(x => x.CustomerEmail).HasMaxLength(256);
        e.Property(x => x.Total).HasPrecision(18, 2);
        e.Property(x => x.FailureReason).HasMaxLength(500);
        e.Property(x => x.RowVersion).IsRowVersion();
    }
}

/// <summary>Used only by `dotnet ef` to build migrations; never connects anywhere.</summary>
public sealed class OrdersDbContextFactory : IDesignTimeDbContextFactory<OrdersDbContext>
{
    public OrdersDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<OrdersDbContext>().UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=design-time-only").Options);
}
