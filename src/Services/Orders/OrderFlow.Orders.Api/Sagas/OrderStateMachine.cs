using MassTransit;
using OrderFlow.Contracts;

namespace OrderFlow.Orders.Api.Sagas;

/// <summary>Persisted saga instance: where one order is in the workflow.</summary>
public sealed class OrderState : SagaStateMachineInstance
{
    public Guid CorrelationId { get; set; }
    public string CurrentState { get; set; } = null!;
    public string CustomerEmail { get; set; } = null!;
    public decimal Total { get; set; }
    public Guid? PaymentId { get; set; }
    public string? FailureReason { get; set; }
    public DateTime SubmittedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>Optimistic concurrency: two events for the same order can't both win.</summary>
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>
/// Orchestrates an order across services:
/// <code>
///   Submitted ──ReserveStock──▶ AwaitingStock ──StockReserved──▶ AwaitingPayment ──PaymentSucceeded──▶ Completed
///                                     │                                │
///                          StockReservationFailed                PaymentFailed ──ReleaseStock (compensate)
///                                     ▼                                ▼
///                                   Failed ◀───────────────────────────┘
/// </code>
/// </summary>
public sealed class OrderStateMachine : MassTransitStateMachine<OrderState>
{
    public State AwaitingStock { get; private set; } = null!;
    public State AwaitingPayment { get; private set; } = null!;
    public State Completed { get; private set; } = null!;
    public State Failed { get; private set; } = null!;

    public Event<OrderSubmitted> OrderSubmitted { get; private set; } = null!;
    public Event<StockReserved> StockReserved { get; private set; } = null!;
    public Event<StockReservationFailed> StockReservationFailed { get; private set; } = null!;
    public Event<PaymentSucceeded> PaymentSucceeded { get; private set; } = null!;
    public Event<PaymentFailed> PaymentFailed { get; private set; } = null!;

    public OrderStateMachine()
    {
        InstanceState(x => x.CurrentState);

        Event(() => OrderSubmitted, e => e.CorrelateById(m => m.Message.OrderId));
        Event(() => StockReserved, e => e.CorrelateById(m => m.Message.OrderId));
        Event(() => StockReservationFailed, e => e.CorrelateById(m => m.Message.OrderId));
        Event(() => PaymentSucceeded, e => e.CorrelateById(m => m.Message.OrderId));
        Event(() => PaymentFailed, e => e.CorrelateById(m => m.Message.OrderId));

        Initially(
            When(OrderSubmitted)
                .Then(c =>
                {
                    c.Saga.CustomerEmail = c.Message.CustomerEmail;
                    c.Saga.Total = c.Message.Total;
                    c.Saga.SubmittedAtUtc = c.Message.SubmittedAtUtc;
                    Touch(c.Saga);
                })
                .Send(Queues.ReserveStock, c => new ReserveStock(c.Message.OrderId, c.Message.Lines))
                .TransitionTo(AwaitingStock));

        During(AwaitingStock,
            When(StockReserved)
                .Then(c => Touch(c.Saga))
                .Send(Queues.ProcessPayment, c => new ProcessPayment(c.Saga.CorrelationId, c.Saga.CustomerEmail, c.Saga.Total))
                .TransitionTo(AwaitingPayment),
            When(StockReservationFailed)
                .Then(c => Fail(c.Saga, c.Message.Reason))
                .Publish(c => new OrderFailed(c.Saga.CorrelationId, c.Message.Reason))
                .TransitionTo(Failed));

        During(AwaitingPayment,
            When(PaymentSucceeded)
                .Then(c =>
                {
                    c.Saga.PaymentId = c.Message.PaymentId;
                    Touch(c.Saga);
                })
                .Publish(c => new OrderCompleted(c.Saga.CorrelationId))
                .TransitionTo(Completed),
            When(PaymentFailed)
                .Then(c => Fail(c.Saga, c.Message.Reason))
                // Compensating action: the stock held in step 1 goes back on the shelf.
                .Send(Queues.ReleaseStock, c => new ReleaseStock(c.Saga.CorrelationId))
                .Publish(c => new OrderFailed(c.Saga.CorrelationId, c.Message.Reason))
                .TransitionTo(Failed));

        // Late or duplicate events after the order is settled are ignored, not errors.
        During(Completed, Failed,
            Ignore(StockReserved), Ignore(StockReservationFailed), Ignore(PaymentSucceeded), Ignore(PaymentFailed));
    }

    private static void Touch(OrderState saga) => saga.UpdatedAtUtc = DateTime.UtcNow;

    private static void Fail(OrderState saga, string reason)
    {
        saga.FailureReason = reason;
        Touch(saga);
    }
}
