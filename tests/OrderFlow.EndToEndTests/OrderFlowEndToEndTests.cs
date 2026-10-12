using System.Net;
using System.Net.Http.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace OrderFlow.EndToEndTests;

/// <summary>
/// Boots the real system from the Aspire AppHost: SQL Server + RabbitMQ containers and all four services.
/// Every request goes through the YARP gateway, exactly like a client would. Requires Docker.
/// </summary>
public sealed class AppFixture : IAsyncLifetime
{
    public DistributedApplication App { get; private set; } = null!;
    public HttpClient Gateway { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.OrderFlow_AppHost>();
        App = await builder.BuildAsync();

        using var startup = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        await App.StartAsync(startup.Token);

        var notifications = App.Services.GetRequiredService<ResourceNotificationService>();
        await notifications.WaitForResourceHealthyAsync("gateway", startup.Token);
        await notifications.WaitForResourceAsync("payments", KnownResourceStates.Running, startup.Token);

        Gateway = App.CreateHttpClient("gateway");
        Gateway.Timeout = TimeSpan.FromSeconds(60);
    }

    public async Task DisposeAsync() => await App.DisposeAsync();
}

public sealed record OrderAccepted(Guid OrderId, string Status);
public sealed record OrderView(Guid Id, string Status, decimal Total, string? FailureReason, Guid? PaymentId);
public sealed record ProductView(string Sku, int OnHand, int Reserved, int Available);

[Trait("Category", "EndToEnd")]
public sealed class OrderFlowEndToEndTests(AppFixture fixture) : IClassFixture<AppFixture>
{
    private HttpClient Gateway => fixture.Gateway;

    [Fact]
    public async Task Paid_order_completes_and_stock_leaves_the_warehouse()
    {
        var before = await ProductAsync("MS-WL-02");

        var order = await PlaceAsync("buyer@example.com", ("MS-WL-02", 2));
        var final = await WaitForSettledAsync(order.OrderId);

        final.Status.ShouldBe("Completed");
        final.PaymentId.ShouldNotBeNull();
        final.Total.ShouldBe(2598m);
        await EventuallyAsync(async () =>
        {
            var after = await ProductAsync("MS-WL-02");
            return after.OnHand == before.OnHand - 2 && after.Reserved == before.Reserved;
        });
    }

    [Fact]
    public async Task Declined_payment_fails_the_order_and_compensates_stock()
    {
        var before = await ProductAsync("CAM-HD-1080");

        var order = await PlaceAsync("decline@example.com", ("CAM-HD-1080", 1));
        var final = await WaitForSettledAsync(order.OrderId);

        final.Status.ShouldBe("Failed");
        final.FailureReason.ShouldBe("Card declined by issuer");
        await EventuallyAsync(async () => (await ProductAsync("CAM-HD-1080")).Available == before.Available);
    }

    [Fact]
    public async Task Order_beyond_available_stock_is_rejected_by_inventory()
    {
        var order = await PlaceAsync("buyer@example.com", ("SSD-NVME-1T", 50));
        var final = await WaitForSettledAsync(order.OrderId);

        final.Status.ShouldBe("Failed");
        final.FailureReason!.ShouldContain("SSD-NVME-1T: requested 50");
    }

    [Fact]
    public async Task Unknown_product_is_a_validation_error()
    {
        var response = await Gateway.PostAsJsonAsync("/api/orders", new
        {
            customerEmail = "buyer@example.com",
            items = new[] { new { sku = "NOPE-404", quantity = 1 } }
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Retrying_with_the_same_idempotency_key_returns_the_same_order()
    {
        var key = Guid.NewGuid().ToString("N");
        var first = await PlaceAsync("buyer@example.com", key, ("HUB-USBC-7", 1));
        var second = await PlaceAsync("buyer@example.com", key, ("HUB-USBC-7", 1));

        second.OrderId.ShouldBe(first.OrderId);
    }

    private Task<OrderAccepted> PlaceAsync(string email, params (string Sku, int Quantity)[] items) => PlaceAsync(email, null, items);

    private async Task<OrderAccepted> PlaceAsync(string email, string? idempotencyKey, params (string Sku, int Quantity)[] items)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
        {
            Content = JsonContent.Create(new { customerEmail = email, items = items.Select(i => new { sku = i.Sku, quantity = i.Quantity }) })
        };
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);

        var response = await Gateway.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        response.Headers.Location.ShouldNotBeNull();
        return (await response.Content.ReadFromJsonAsync<OrderAccepted>())!;
    }

    private async Task<OrderView> WaitForSettledAsync(Guid orderId)
    {
        OrderView? view = null;
        await EventuallyAsync(async () =>
        {
            view = await Gateway.GetFromJsonAsync<OrderView>($"/api/orders/{orderId}");
            return view!.Status is "Completed" or "Failed";
        });
        return view!;
    }

    private async Task<ProductView> ProductAsync(string sku) => (await Gateway.GetFromJsonAsync<ProductView>($"/api/products/{sku}"))!;

    private static async Task EventuallyAsync(Func<Task<bool>> condition, int timeoutSeconds = 90)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(500);
        }
        throw new TimeoutException($"Condition not met within {timeoutSeconds}s.");
    }
}
