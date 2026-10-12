namespace OrderFlow.Contracts;

// Messages exchanged over RabbitMQ. Events (past tense) are published to any interested service;
// commands (imperative) are sent to the one queue that owns the work.
// Every message carries the OrderId, which is also the saga correlation id.

public sealed record OrderLine(string Sku, int Quantity, decimal UnitPrice)
{
    public decimal LineTotal => Quantity * UnitPrice;
}

// ---- events -------------------------------------------------------------------------------

public sealed record OrderSubmitted(Guid OrderId, string CustomerEmail, IReadOnlyList<OrderLine> Lines, decimal Total, DateTime SubmittedAtUtc);

public sealed record StockReserved(Guid OrderId);

public sealed record StockReservationFailed(Guid OrderId, string Reason);

public sealed record PaymentSucceeded(Guid OrderId, Guid PaymentId);

public sealed record PaymentFailed(Guid OrderId, string Reason);

public sealed record OrderCompleted(Guid OrderId);

public sealed record OrderFailed(Guid OrderId, string Reason);

// ---- commands -----------------------------------------------------------------------------

public sealed record ReserveStock(Guid OrderId, IReadOnlyList<OrderLine> Lines);

public sealed record ReleaseStock(Guid OrderId);

public sealed record ProcessPayment(Guid OrderId, string CustomerEmail, decimal Amount);

/// <summary>Queue addresses for commands (kebab-case consumer names).</summary>
public static class Queues
{
    public static readonly Uri ReserveStock = new("queue:reserve-stock");
    public static readonly Uri ReleaseStock = new("queue:release-stock");
    public static readonly Uri ProcessPayment = new("queue:process-payment");
}
