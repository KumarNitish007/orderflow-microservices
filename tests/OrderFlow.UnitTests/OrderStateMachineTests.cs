using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using OrderFlow.Contracts;
using OrderFlow.Orders.Api.Sagas;
using Shouldly;

namespace OrderFlow.UnitTests;

/// <summary>The saga in isolation: feed it events, assert the commands it sends and the state it reaches.</summary>
public sealed class OrderStateMachineTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;
    private ITestHarness _harness = null!;
    private ISagaStateMachineTestHarness<OrderStateMachine, OrderState> _saga = null!;

    public async Task InitializeAsync()
    {
        _provider = new ServiceCollection()
            .AddMassTransitTestHarness(x =>
            {
                x.SetKebabCaseEndpointNameFormatter();
                x.AddSagaStateMachine<OrderStateMachine, OrderState>().InMemoryRepository();
            })
            .BuildServiceProvider(true);
        _harness = _provider.GetRequiredService<ITestHarness>();
        await _harness.Start();
        _saga = _harness.GetSagaStateMachineHarness<OrderStateMachine, OrderState>();
    }

    public async Task DisposeAsync()
    {
        await _harness.Stop();
        await _provider.DisposeAsync();
    }

    private async Task<Guid> SubmitAsync(decimal total = 2598m)
    {
        var id = NewId.NextGuid();
        await _harness.Bus.Publish(new OrderSubmitted(id, "buyer@test.local", [new OrderLine("MS-WL-02", 2, 1299m)], total, DateTime.UtcNow));
        (await _saga.Exists(id, m => m.AwaitingStock)).ShouldNotBeNull();
        return id;
    }

    [Fact]
    public async Task Submitting_an_order_asks_inventory_to_reserve_stock()
    {
        var id = await SubmitAsync();

        (await _harness.Sent.Any<ReserveStock>(m => m.Context.Message.OrderId == id)).ShouldBeTrue();
        var instance = _saga.Sagas.Contains(id);
        instance.CustomerEmail.ShouldBe("buyer@test.local");
        instance.Total.ShouldBe(2598m);
    }

    [Fact]
    public async Task Reserved_stock_triggers_payment_and_payment_completes_the_order()
    {
        var id = await SubmitAsync();

        await _harness.Bus.Publish(new StockReserved(id));
        (await _saga.Exists(id, m => m.AwaitingPayment)).ShouldNotBeNull();
        (await _harness.Sent.Any<ProcessPayment>(m => m.Context.Message.OrderId == id && m.Context.Message.Amount == 2598m)).ShouldBeTrue();

        var paymentId = Guid.NewGuid();
        await _harness.Bus.Publish(new PaymentSucceeded(id, paymentId));
        (await _saga.Exists(id, m => m.Completed)).ShouldNotBeNull();
        (await _harness.Published.Any<OrderCompleted>(m => m.Context.Message.OrderId == id)).ShouldBeTrue();
        _saga.Sagas.Contains(id).PaymentId.ShouldBe(paymentId);
    }

    [Fact]
    public async Task Failed_reservation_fails_the_order_without_charging()
    {
        var id = await SubmitAsync();

        await _harness.Bus.Publish(new StockReservationFailed(id, "MS-WL-02: requested 2, available 0"));

        (await _saga.Exists(id, m => m.Failed)).ShouldNotBeNull();
        (await _harness.Published.Any<OrderFailed>(m => m.Context.Message.OrderId == id)).ShouldBeTrue();
        (await _harness.Sent.Any<ProcessPayment>(m => m.Context.Message.OrderId == id)).ShouldBeFalse();
        _saga.Sagas.Contains(id).FailureReason!.ShouldContain("available 0");
    }

    [Fact]
    public async Task Failed_payment_compensates_by_releasing_the_reserved_stock()
    {
        var id = await SubmitAsync();
        await _harness.Bus.Publish(new StockReserved(id));
        (await _saga.Exists(id, m => m.AwaitingPayment)).ShouldNotBeNull();

        await _harness.Bus.Publish(new PaymentFailed(id, "Card declined by issuer"));

        (await _saga.Exists(id, m => m.Failed)).ShouldNotBeNull();
        (await _harness.Sent.Any<ReleaseStock>(m => m.Context.Message.OrderId == id)).ShouldBeTrue();
        (await _harness.Published.Any<OrderFailed>(m => m.Context.Message.OrderId == id)).ShouldBeTrue();
    }

    [Fact]
    public async Task Late_duplicate_events_after_completion_are_ignored()
    {
        var id = await SubmitAsync();
        await _harness.Bus.Publish(new StockReserved(id));
        (await _saga.Exists(id, m => m.AwaitingPayment)).ShouldNotBeNull();
        await _harness.Bus.Publish(new PaymentSucceeded(id, Guid.NewGuid()));
        (await _saga.Exists(id, m => m.Completed)).ShouldNotBeNull();

        await _harness.Bus.Publish(new PaymentFailed(id, "duplicate from a retry"));
        await _harness.InactivityTask;

        (await _saga.Exists(id, m => m.Completed)).ShouldNotBeNull();
        (await _harness.Sent.Any<ReleaseStock>(m => m.Context.Message.OrderId == id)).ShouldBeFalse();
    }
}
