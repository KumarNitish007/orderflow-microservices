using OrderFlow.Catalog.Api.Domain;
using OrderFlow.Payments.Worker;
using Shouldly;

namespace OrderFlow.UnitTests;

public sealed class ProductTests
{
    [Fact]
    public void Reserving_holds_stock_without_removing_it()
    {
        var p = new Product("SKU", "Thing", 100m, 10);

        p.Reserve(4);

        p.OnHand.ShouldBe(10);
        p.Reserved.ShouldBe(4);
        p.Available.ShouldBe(6);
    }

    [Fact]
    public void Cannot_reserve_more_than_available()
    {
        var p = new Product("SKU", "Thing", 100m, 3);
        p.Reserve(2);

        Should.Throw<InsufficientStockException>(() => p.Reserve(2)).Message.ShouldBe("SKU: requested 2, available 1");
    }

    [Fact]
    public void Commit_removes_reserved_units_and_release_returns_them()
    {
        var p = new Product("SKU", "Thing", 100m, 10);
        p.Reserve(5);

        p.Commit(3);
        p.Release(2);

        p.OnHand.ShouldBe(7);
        p.Reserved.ShouldBe(0);
        p.Available.ShouldBe(7);
    }

    [Fact]
    public void Every_stock_change_rotates_the_concurrency_token()
    {
        var p = new Product("SKU", "Thing", 100m, 10);
        var before = p.Version;

        p.Reserve(1);

        p.Version.ShouldNotBe(before);
    }
}

public sealed class SimulatedPaymentGatewayTests
{
    private readonly SimulatedPaymentGateway _gateway = new();

    [Theory]
    [InlineData("buyer@test.local", 4499, true, null)]
    [InlineData("buyer@test.local", 100000, true, null)]
    [InlineData("buyer@test.local", 100000.01, false, "Amount exceeds card limit of 100,000")]
    [InlineData("DECLINE@test.local", 10, false, "Card declined by issuer")]
    [InlineData("buyer@test.local", 0, false, "Invalid amount")]
    public async Task Decides_deterministically(string email, decimal amount, bool approved, string? reason)
    {
        var decision = await _gateway.ChargeAsync(Guid.NewGuid(), email, amount, CancellationToken.None);

        decision.Approved.ShouldBe(approved);
        decision.Reason.ShouldBe(reason);
    }
}

public sealed class OrderValidationTests
{
    [Fact]
    public void Valid_order_has_no_errors() =>
        OrderValidation.Validate(new PlaceOrderRequest("a@b.co", [new PlaceOrderItem("SKU", 1)])).ShouldBeEmpty();

    [Theory]
    [InlineData("not-an-email", "SKU", 1, "customerEmail")]
    [InlineData("a@b.co", "", 1, "items")]
    [InlineData("a@b.co", "SKU", 0, "items")]
    [InlineData("a@b.co", "SKU", 1001, "items")]
    public void Invalid_orders_are_rejected(string email, string sku, int quantity, string field) =>
        OrderValidation.Validate(new PlaceOrderRequest(email, [new PlaceOrderItem(sku, quantity)])).Keys.ShouldContain(field);

    [Fact]
    public void Empty_orders_are_rejected() =>
        OrderValidation.Validate(new PlaceOrderRequest("a@b.co", [])).Keys.ShouldContain("items");
}
