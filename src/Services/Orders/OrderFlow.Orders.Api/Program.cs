using System.Net.Mail;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using OrderFlow.Contracts;
using OrderFlow.Orders.Api.Data;
using OrderFlow.Orders.Api.Sagas;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddSqlServerDbContext<OrdersDbContext>("ordersdb", s => s.DisableRetry = true);
builder.AddMessaging<OrdersDbContext>(x =>
{
    x.AddSagaStateMachine<OrderStateMachine, OrderState>()
        .EntityFrameworkRepository(r =>
        {
            r.ConcurrencyMode = ConcurrencyMode.Optimistic;
            r.ExistingDbContext<OrdersDbContext>();
            r.UseSqlServer();
        });
});

// Typed client for the Catalog service. "http://catalog" is resolved by Aspire service discovery,
// and the standard resilience handler (retry, timeout, circuit breaker) comes from ServiceDefaults.
builder.Services.AddHttpClient<CatalogClient>(c => c.BaseAddress = new Uri("http://catalog"));

builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c => c.SwaggerDoc("v1", new() { Title = "Orders API", Version = "v1" }));

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
    await scope.ServiceProvider.GetRequiredService<OrdersDbContext>().Database.MigrateAsync();

app.UseExceptionHandler();
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapDefaultEndpoints();
app.MapOrderEndpoints();

app.Run();

public sealed record PlaceOrderRequest(string CustomerEmail, IReadOnlyList<PlaceOrderItem> Items);
public sealed record PlaceOrderItem(string Sku, int Quantity);
public sealed record OrderView(
    Guid Id, string CustomerEmail, string Status, decimal Total, string? FailureReason, Guid? PaymentId,
    DateTime CreatedAtUtc, DateTime? UpdatedAtUtc, IReadOnlyList<OrderItem> Items);

public static class OrderValidation
{
    public const int MaxLines = 50;
    public const int MaxQuantityPerLine = 1000;

    public static Dictionary<string, string[]> Validate(PlaceOrderRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.CustomerEmail) || !MailAddress.TryCreate(request.CustomerEmail, out _))
            errors["customerEmail"] = ["A valid email address is required."];
        if (request.Items is null || request.Items.Count == 0)
            errors["items"] = ["An order needs at least one item."];
        else if (request.Items.Count > MaxLines)
            errors["items"] = [$"An order can have at most {MaxLines} lines."];
        else if (request.Items.Any(i => string.IsNullOrWhiteSpace(i.Sku) || i.Quantity is < 1 or > MaxQuantityPerLine))
            errors["items"] = [$"Every item needs a SKU and a quantity between 1 and {MaxQuantityPerLine}."];
        return errors;
    }
}

internal static class OrderEndpoints
{
    public static void MapOrderEndpoints(this IEndpointRouteBuilder app)
    {
        var orders = app.MapGroup("/api/orders").WithTags("Orders");

        orders.MapPost("/", PlaceOrderAsync)
            .WithSummary("Accept an order and start the fulfilment workflow")
            .WithDescription("Returns 202 Accepted immediately; poll the Location URL for status. Send an Idempotency-Key header to make retries safe.");

        orders.MapGet("/{id:guid}", async (Guid id, OrdersDbContext db, CancellationToken ct) =>
            (await LoadViewsAsync(db, db.Orders.Where(o => o.Id == id), ct)).FirstOrDefault() is { } view
                ? Results.Ok(view)
                : Results.NotFound());

        orders.MapGet("/", async (string? email, OrdersDbContext db, CancellationToken ct) =>
        {
            var q = db.Orders.AsQueryable();
            if (!string.IsNullOrWhiteSpace(email))
            {
                var normalized = email.Trim().ToLowerInvariant();
                q = q.Where(o => o.CustomerEmail == normalized);
            }
            return await LoadViewsAsync(db, q.OrderByDescending(o => o.CreatedAtUtc).Take(50), ct);
        });
    }

    private static async Task<IResult> PlaceOrderAsync(
        PlaceOrderRequest request,
        HttpRequest http,
        OrdersDbContext db,
        CatalogClient catalog,
        IPublishEndpoint publish,
        CancellationToken ct)
    {
        var errors = OrderValidation.Validate(request);
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        var idempotencyKey = http.Headers["Idempotency-Key"].FirstOrDefault()?.Trim();
        if (idempotencyKey is { Length: > 100 })
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["Idempotency-Key"] = ["Must be at most 100 characters."] });

        if (idempotencyKey is not null &&
            await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.IdempotencyKey == idempotencyKey, ct) is { } replay)
        {
            return Results.Accepted($"/api/orders/{replay.Id}", new { orderId = replay.Id, status = "Submitted", replayed = true });
        }

        // Synchronous call: prices come from the Catalog service, never from the client.
        var quote = await catalog.QuoteAsync(request.Items, ct);
        if (quote.Error is not null)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["items"] = [quote.Error] });

        var order = new Order(NewId.NextGuid(), request.CustomerEmail.Trim().ToLowerInvariant(),
            quote.Lines.Select(l => new OrderItem(l.Sku, l.Name, l.Quantity, l.UnitPrice)), idempotencyKey);
        db.Orders.Add(order);

        // With the bus outbox this message is written to the OutboxMessage table in the SAME transaction
        // as the order, and only delivered to RabbitMQ after commit.
        await publish.Publish(new OrderSubmitted(order.Id, order.CustomerEmail,
            order.Items.Select(i => new OrderLine(i.Sku, i.Quantity, i.UnitPrice)).ToList(),
            order.Total, order.CreatedAtUtc), ct);

        await db.SaveChangesAsync(ct);

        return Results.Accepted($"/api/orders/{order.Id}", new { orderId = order.Id, status = "Submitted", total = order.Total });
    }

    /// <summary>Order details plus the saga's view of progress. Two simple indexed queries; the saga row
    /// may not exist yet in the moments between accepting an order and the saga consuming OrderSubmitted.</summary>
    internal static async Task<List<OrderView>> LoadViewsAsync(OrdersDbContext db, IQueryable<Order> orders, CancellationToken ct)
    {
        var list = await orders.AsNoTracking().ToListAsync(ct);
        var ids = list.Select(o => o.Id).ToList();
        var states = await db.OrderStates.AsNoTracking()
            .Where(s => ids.Contains(s.CorrelationId))
            .ToDictionaryAsync(s => s.CorrelationId, ct);

        return list.Select(o =>
        {
            var s = states.GetValueOrDefault(o.Id);
            return new OrderView(o.Id, o.CustomerEmail, s?.CurrentState ?? "Submitted", o.Total, s?.FailureReason,
                s?.PaymentId, o.CreatedAtUtc, s?.UpdatedAtUtc, o.Items);
        }).ToList();
    }
}

public sealed record CatalogQuoteLine(string Sku, string Name, int Quantity, decimal UnitPrice);

public sealed class CatalogClient(HttpClient http)
{
    public async Task<(IReadOnlyList<CatalogQuoteLine> Lines, string? Error)> QuoteAsync(IReadOnlyList<PlaceOrderItem> items, CancellationToken ct)
    {
        var response = await http.PostAsJsonAsync("/api/products/quote", new { items }, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            var problem = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ValidationProblemDetails>(ct);
            return ([], problem?.Errors.Values.SelectMany(v => v).FirstOrDefault() ?? "Invalid items.");
        }

        response.EnsureSuccessStatusCode();
        return ((await response.Content.ReadFromJsonAsync<List<CatalogQuoteLine>>(ct))!, null);
    }
}
