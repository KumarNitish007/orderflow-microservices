using MassTransit;
using Microsoft.EntityFrameworkCore;
using OrderFlow.Catalog.Api.Data;
using OrderFlow.Catalog.Api.Domain;
using OrderFlow.Contracts;

namespace OrderFlow.Catalog.Api.Consumers;

/// <summary>Holds stock for every line of an order, all or nothing.</summary>
public sealed class ReserveStockConsumer(CatalogDbContext db, ILogger<ReserveStockConsumer> logger) : IConsumer<ReserveStock>
{
    public async Task Consume(ConsumeContext<ReserveStock> context)
    {
        var message = context.Message;

        // Idempotency: a redelivered command repeats the original answer instead of reserving twice.
        var existing = await db.Reservations.AsNoTracking().FirstOrDefaultAsync(r => r.OrderId == message.OrderId, context.CancellationToken);
        if (existing is not null)
        {
            await (existing.Status == ReservationStatus.Rejected
                ? context.Publish(new StockReservationFailed(message.OrderId, existing.Reason ?? "Stock unavailable"))
                : context.Publish(new StockReserved(message.OrderId)));
            return;
        }

        var requested = message.Lines
            .GroupBy(l => l.Sku)
            .Select(g => new ReservationLine(g.Key, g.Sum(l => l.Quantity)))
            .ToList();
        var skus = requested.Select(l => l.Sku).ToList();
        var products = await db.Products.Where(p => skus.Contains(p.Sku)).ToDictionaryAsync(p => p.Sku, context.CancellationToken);

        var problems = requested
            .Select(l => !products.TryGetValue(l.Sku, out var p) ? $"{l.Sku}: unknown product"
                : p.Available < l.Quantity ? $"{l.Sku}: requested {l.Quantity}, available {p.Available}"
                : null)
            .OfType<string>()
            .ToList();

        if (problems.Count > 0)
        {
            var reason = string.Join("; ", problems);
            db.Reservations.Add(new Reservation(message.OrderId, ReservationStatus.Rejected, requested, reason));
            await context.Publish(new StockReservationFailed(message.OrderId, reason));
            logger.LogInformation("Rejected reservation for order {OrderId}: {Reason}", message.OrderId, reason);
        }
        else
        {
            foreach (var line in requested) products[line.Sku].Reserve(line.Quantity);
            db.Reservations.Add(new Reservation(message.OrderId, ReservationStatus.Reserved, requested));
            await context.Publish(new StockReserved(message.OrderId));
            logger.LogInformation("Reserved {Lines} line(s) for order {OrderId}", requested.Count, message.OrderId);
        }

        // Saved together with the outgoing message (outbox), in one transaction.
        // A concurrent reservation on the same product fails the concurrency check and is retried.
        await db.SaveChangesAsync(context.CancellationToken);
    }
}

/// <summary>Compensation after a failed payment: release what was held.</summary>
public sealed class ReleaseStockConsumer(CatalogDbContext db) : IConsumer<ReleaseStock>
{
    public Task Consume(ConsumeContext<ReleaseStock> context) =>
        StockTransitions.ApplyAsync(db, context.Message.OrderId, ReservationStatus.Released, (p, q) => p.Release(q), context.CancellationToken);
}

/// <summary>The order is paid: reserved units are now sold.</summary>
public sealed class OrderCompletedConsumer(CatalogDbContext db) : IConsumer<OrderCompleted>
{
    public Task Consume(ConsumeContext<OrderCompleted> context) =>
        StockTransitions.ApplyAsync(db, context.Message.OrderId, ReservationStatus.Committed, (p, q) => p.Commit(q), context.CancellationToken);
}

internal static class StockTransitions
{
    public static async Task ApplyAsync(CatalogDbContext db, Guid orderId, ReservationStatus target,
        Action<Product, int> apply, CancellationToken ct)
    {
        var reservation = await db.Reservations.FirstOrDefaultAsync(r => r.OrderId == orderId, ct);

        // Only a live reservation moves; anything else is a duplicate or a no-op.
        if (reservation is not { Status: ReservationStatus.Reserved }) return;

        var skus = reservation.Lines.Select(l => l.Sku).ToList();
        var products = await db.Products.Where(p => skus.Contains(p.Sku)).ToDictionaryAsync(p => p.Sku, ct);
        foreach (var line in reservation.Lines) apply(products[line.Sku], line.Quantity);

        reservation.Status = target;
        reservation.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}
