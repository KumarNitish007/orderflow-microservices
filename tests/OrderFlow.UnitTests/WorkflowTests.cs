using MassTransit;
using MassTransit.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderFlow.Catalog.Api.Consumers;
using OrderFlow.Catalog.Api.Data;
using OrderFlow.Catalog.Api.Domain;
using OrderFlow.Contracts;
using OrderFlow.Orders.Api.Sagas;
using OrderFlow.Payments.Worker;
using Shouldly;

namespace OrderFlow.UnitTests;

/// <summary>
/// The whole distributed workflow in one process: the real saga, the real Catalog consumers and the real
/// Payments consumer, wired to an in-memory bus with SQLite databases. It proves the services agree on
/// the message contracts and queue names. (The end-to-end suite repeats this over RabbitMQ + SQL Server.)
/// </summary>
public sealed class WorkflowTests : IAsyncLifetime
{
    private readonly SqliteConnection _catalogDb = new("DataSource=:memory:");
    private readonly SqliteConnection _paymentsDb = new("DataSource=:memory:");
    private ServiceProvider _provider = null!;
    private ITestHarness _harness = null!;
    private ISagaStateMachineTestHarness<OrderStateMachine, OrderState> _saga = null!;

    public async Task InitializeAsync()
    {
        await _catalogDb.OpenAsync();
        await _paymentsDb.OpenAsync();

        _provider = new ServiceCollection()
            .AddLogging()
            .AddDbContext<CatalogDbContext>(o => o.UseSqlite(_catalogDb))
            .AddDbContext<PaymentsDbContext>(o => o.UseSqlite(_paymentsDb))
            .AddSingleton<IPaymentGateway, SimulatedPaymentGateway>()
            .AddMassTransitTestHarness(x =>
            {
                x.SetKebabCaseEndpointNameFormatter();
                x.AddSagaStateMachine<OrderStateMachine, OrderState>().InMemoryRepository();
                x.AddConsumer<ReserveStockConsumer>();
                x.AddConsumer<ReleaseStockConsumer>();
                x.AddConsumer<OrderCompletedConsumer>();
                x.AddConsumer<ProcessPaymentConsumer>();
            })
            .BuildServiceProvider(true);

        await using (var scope = _provider.CreateAsyncScope())
        {
            var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            await catalog.Database.EnsureCreatedAsync();
            catalog.Products.AddRange(new Product("KB-MECH-01", "Keyboard", 4499m, 10), new Product("MON-27-4K", "Monitor", 28999m, 5));
            await catalog.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().Database.EnsureCreatedAsync();
        }

        _harness = _provider.GetRequiredService<ITestHarness>();
        await _harness.Start();
        _saga = _harness.GetSagaStateMachineHarness<OrderStateMachine, OrderState>();
    }

    public async Task DisposeAsync()
    {
        await _harness.Stop();
        await _provider.DisposeAsync();
        await _catalogDb.DisposeAsync();
        await _paymentsDb.DisposeAsync();
    }

    private async Task<Guid> PlaceAsync(string email, params OrderLine[] lines)
    {
        var id = NewId.NextGuid();
        await _harness.Bus.Publish(new OrderSubmitted(id, email, lines, lines.Sum(l => l.LineTotal), DateTime.UtcNow));
        return id;
    }

    private async Task<Product> ProductAsync(string sku)
    {
        await _harness.InactivityTask; // let in-flight messages settle
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<CatalogDbContext>().Products.AsNoTracking().SingleAsync(p => p.Sku == sku);
    }

    [Fact]
    public async Task Happy_path_reserves_charges_completes_and_commits_stock()
    {
        var id = await PlaceAsync("buyer@test.local", new OrderLine("KB-MECH-01", 3, 4499m));

        (await _saga.Exists(id, m => m.Completed)).ShouldNotBeNull();
        var keyboard = await ProductAsync("KB-MECH-01");
        keyboard.OnHand.ShouldBe(7);
        keyboard.Reserved.ShouldBe(0);

        await using var scope = _provider.CreateAsyncScope();
        var payment = await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().Payments.SingleAsync(p => p.OrderId == id);
        payment.Status.ShouldBe(PaymentStatus.Succeeded);
        _saga.Sagas.Contains(id).PaymentId.ShouldBe(payment.Id);
    }

    [Fact]
    public async Task Declined_payment_releases_the_reservation()
    {
        var id = await PlaceAsync("decline-me@test.local", new OrderLine("MON-27-4K", 2, 28999m));

        (await _saga.Exists(id, m => m.Failed)).ShouldNotBeNull();
        _saga.Sagas.Contains(id).FailureReason.ShouldBe("Card declined by issuer");
        var monitor = await ProductAsync("MON-27-4K");
        monitor.OnHand.ShouldBe(5);
        monitor.Reserved.ShouldBe(0);
    }

    [Fact]
    public async Task Insufficient_stock_fails_the_order_and_never_charges()
    {
        var id = await PlaceAsync("buyer@test.local", new OrderLine("MON-27-4K", 6, 28999m));

        (await _saga.Exists(id, m => m.Failed)).ShouldNotBeNull();
        _saga.Sagas.Contains(id).FailureReason.ShouldBe("MON-27-4K: requested 6, available 5");
        (await _harness.Consumed.Any<ProcessPayment>(m => m.Context.Message.OrderId == id)).ShouldBeFalse();
        (await ProductAsync("MON-27-4K")).Reserved.ShouldBe(0);
    }

    [Fact]
    public async Task Card_limit_is_enforced_by_payments_and_stock_is_returned()
    {
        // 4 monitors = 1,15,996 > card limit of 1,00,000
        var id = await PlaceAsync("buyer@test.local", new OrderLine("MON-27-4K", 4, 28999m));

        (await _saga.Exists(id, m => m.Failed)).ShouldNotBeNull();
        _saga.Sagas.Contains(id).FailureReason!.ShouldStartWith("Amount exceeds card limit");
        (await ProductAsync("MON-27-4K")).Available.ShouldBe(5);
    }

    [Fact]
    public async Task A_redelivered_reserve_command_does_not_reserve_twice()
    {
        var id = NewId.NextGuid();
        var command = new ReserveStock(id, [new OrderLine("KB-MECH-01", 2, 4499m)]);
        var endpoint = await _harness.Bus.GetSendEndpoint(Queues.ReserveStock);

        await endpoint.Send(command);
        await endpoint.Send(command);

        (await ProductAsync("KB-MECH-01")).Reserved.ShouldBe(2);
        (await _harness.Published.SelectAsync<StockReserved>(m => m.Context.Message.OrderId == id).Count()).ShouldBe(2);
    }
}
