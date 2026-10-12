using System.Globalization;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using OrderFlow.Contracts;

namespace OrderFlow.Payments.Worker;

public enum PaymentStatus
{
    Succeeded,
    Failed
}

public sealed class Payment
{
    private Payment() { }

    public Payment(Guid orderId, decimal amount, PaymentStatus status, string? failureReason)
    {
        OrderId = orderId;
        Amount = amount;
        Status = status;
        FailureReason = failureReason;
    }

    public Guid Id { get; private set; } = NewId.NextGuid();
    public Guid OrderId { get; private set; }
    public decimal Amount { get; private set; }
    public PaymentStatus Status { get; private set; }
    public string? FailureReason { get; private set; }
    public DateTime CreatedAtUtc { get; private set; } = DateTime.UtcNow;
}

public sealed class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options) : DbContext(options)
{
    public DbSet<Payment> Payments => Set<Payment>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema("payments");
        b.Entity<Payment>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.Id).ValueGeneratedNever();
            e.HasIndex(p => p.OrderId).IsUnique(); // one charge per order, enforced by the database
            e.Property(p => p.Amount).HasPrecision(18, 2);
            e.Property(p => p.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(p => p.FailureReason).HasMaxLength(300);
        });

        b.AddInboxStateEntity();
        b.AddOutboxMessageEntity();
        b.AddOutboxStateEntity();
    }
}

public sealed class PaymentsDbContextFactory : IDesignTimeDbContextFactory<PaymentsDbContext>
{
    public PaymentsDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<PaymentsDbContext>().UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=design-time-only").Options);
}

public sealed record PaymentDecision(bool Approved, string? Reason);

public interface IPaymentGateway
{
    Task<PaymentDecision> ChargeAsync(Guid orderId, string customerEmail, decimal amount, CancellationToken ct);
}

/// <summary>
/// Stand-in for a real PSP so the demo is deterministic:
/// amounts above the card limit, or customer emails starting with "decline", are declined.
/// </summary>
public sealed class SimulatedPaymentGateway : IPaymentGateway
{
    public const decimal CardLimit = 100_000m;

    public async Task<PaymentDecision> ChargeAsync(Guid orderId, string customerEmail, decimal amount, CancellationToken ct)
    {
        await Task.Delay(150, ct); // network latency, visible in traces

        if (amount <= 0) return new(false, "Invalid amount");
        if (amount > CardLimit) return new(false, string.Create(CultureInfo.InvariantCulture, $"Amount exceeds card limit of {CardLimit:N0}"));
        if (customerEmail.StartsWith("decline", StringComparison.OrdinalIgnoreCase)) return new(false, "Card declined by issuer");
        return new(true, null);
    }
}

public sealed class ProcessPaymentConsumer(PaymentsDbContext db, IPaymentGateway gateway, ILogger<ProcessPaymentConsumer> logger)
    : IConsumer<ProcessPayment>
{
    public async Task Consume(ConsumeContext<ProcessPayment> context)
    {
        var m = context.Message;

        // Never charge twice: a repeated command replays the stored outcome.
        var payment = await db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.OrderId == m.OrderId, context.CancellationToken);
        if (payment is null)
        {
            var decision = await gateway.ChargeAsync(m.OrderId, m.CustomerEmail, m.Amount, context.CancellationToken);
            payment = new Payment(m.OrderId, m.Amount, decision.Approved ? PaymentStatus.Succeeded : PaymentStatus.Failed, decision.Reason);
            db.Payments.Add(payment);
            logger.LogInformation("Payment for order {OrderId}: {Status} {Reason}", m.OrderId, payment.Status, decision.Reason);
        }

        await (payment.Status == PaymentStatus.Succeeded
            ? context.Publish(new PaymentSucceeded(m.OrderId, payment.Id))
            : context.Publish(new PaymentFailed(m.OrderId, payment.FailureReason ?? "Payment failed")));

        await db.SaveChangesAsync(context.CancellationToken);
    }
}
