namespace OrderFlow.Catalog.Api.Domain;

public sealed class InsufficientStockException(string message) : Exception(message);

/// <summary>
/// Stock is tracked as OnHand (physically in the warehouse) and Reserved (held for orders that are
/// still being paid for). Available = OnHand - Reserved is what new orders may take.
/// </summary>
public sealed class Product
{
    private Product() { }

    public Product(string sku, string name, decimal price, int onHand)
    {
        if (price < 0) throw new ArgumentOutOfRangeException(nameof(price));
        if (onHand < 0) throw new ArgumentOutOfRangeException(nameof(onHand));
        Sku = sku;
        Name = name;
        Price = price;
        OnHand = onHand;
    }

    public string Sku { get; private set; } = null!;
    public string Name { get; private set; } = null!;
    public decimal Price { get; private set; }
    public int OnHand { get; private set; }
    public int Reserved { get; private set; }

    /// <summary>Optimistic-concurrency token, regenerated on every stock change (portable across providers).</summary>
    public Guid Version { get; private set; } = Guid.NewGuid();

    public int Available => OnHand - Reserved;

    public void Reserve(int quantity)
    {
        if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity));
        if (quantity > Available) throw new InsufficientStockException($"{Sku}: requested {quantity}, available {Available}");
        Reserved += quantity;
        Touch();
    }

    /// <summary>Compensation: the order failed, give the held units back.</summary>
    public void Release(int quantity)
    {
        Reserved -= Math.Min(quantity, Reserved);
        Touch();
    }

    /// <summary>The order completed: held units leave the warehouse.</summary>
    public void Commit(int quantity)
    {
        var take = Math.Min(quantity, Reserved);
        Reserved -= take;
        OnHand -= take;
        Touch();
    }

    public void Restock(int quantity)
    {
        if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity));
        OnHand += quantity;
        Touch();
    }

    private void Touch() => Version = Guid.NewGuid();
}

public enum ReservationStatus
{
    Reserved,
    Rejected,
    Released,
    Committed
}

/// <summary>What was held for one order. Keyed by OrderId, which makes every handler idempotent.</summary>
public sealed class Reservation
{
    private Reservation() { }

    public Reservation(Guid orderId, ReservationStatus status, IEnumerable<ReservationLine> lines, string? reason = null)
    {
        OrderId = orderId;
        Status = status;
        Reason = reason;
        Lines = lines.ToList();
    }

    public Guid OrderId { get; private set; }
    public ReservationStatus Status { get; set; }
    public string? Reason { get; private set; }
    public DateTime CreatedAtUtc { get; private set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
    public List<ReservationLine> Lines { get; private set; } = [];
}

public sealed record ReservationLine(string Sku, int Quantity);
