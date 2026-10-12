using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using OrderFlow.Catalog.Api.Consumers;
using OrderFlow.Catalog.Api.Data;
using OrderFlow.Catalog.Api.Domain;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
// Connection string "catalogdb" is injected by the Aspire AppHost; nothing is configured in source.
builder.AddSqlServerDbContext<CatalogDbContext>("catalogdb", s => s.DisableRetry = true);
builder.AddMessaging<CatalogDbContext>(x =>
{
    x.AddConsumer<ReserveStockConsumer>();
    x.AddConsumer<ReleaseStockConsumer>();
    x.AddConsumer<OrderCompletedConsumer>();
});

builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c => c.SwaggerDoc("v1", new() { Title = "Catalog API", Version = "v1" }));

var app = builder.Build();

await CatalogSeeder.MigrateAndSeedAsync(app.Services);

app.UseExceptionHandler();
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapDefaultEndpoints();
app.MapProductEndpoints();

app.Run();

public sealed record ProductDto(string Sku, string Name, decimal Price, int OnHand, int Reserved, int Available);
public sealed record QuoteRequest(IReadOnlyList<QuoteItem> Items);
public sealed record QuoteItem(string Sku, int Quantity);
public sealed record QuoteLine(string Sku, string Name, int Quantity, decimal UnitPrice);
public sealed record RestockRequest(int Quantity);

internal static class ProductEndpoints
{
    public static void MapProductEndpoints(this IEndpointRouteBuilder app)
    {
        var products = app.MapGroup("/api/products").WithTags("Products");

        products.MapGet("/", async (CatalogDbContext db, CancellationToken ct) =>
            await db.Products.AsNoTracking().OrderBy(p => p.Name)
                .Select(p => new ProductDto(p.Sku, p.Name, p.Price, p.OnHand, p.Reserved, p.OnHand - p.Reserved))
                .ToListAsync(ct));

        products.MapGet("/{sku}", async Task<Results<Ok<ProductDto>, NotFound>> (string sku, CatalogDbContext db, CancellationToken ct) =>
            await db.Products.AsNoTracking().Where(p => p.Sku == sku)
                    .Select(p => new ProductDto(p.Sku, p.Name, p.Price, p.OnHand, p.Reserved, p.OnHand - p.Reserved))
                    .FirstOrDefaultAsync(ct) is { } dto
                ? TypedResults.Ok(dto)
                : TypedResults.NotFound());

        // Authoritative prices for an order (the Orders service never trusts client-supplied prices).
        products.MapPost("/quote", async (QuoteRequest request, CatalogDbContext db, CancellationToken ct) =>
        {
            var skus = request.Items.Select(i => i.Sku).Distinct().ToList();
            var known = await db.Products.AsNoTracking().Where(p => skus.Contains(p.Sku)).ToDictionaryAsync(p => p.Sku, ct);

            var unknown = skus.Where(s => !known.ContainsKey(s)).ToList();
            if (unknown.Count > 0)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["items"] = [$"Unknown SKU(s): {string.Join(", ", unknown)}"] });

            return Results.Ok(request.Items.Select(i => new QuoteLine(i.Sku, known[i.Sku].Name, i.Quantity, known[i.Sku].Price)));
        });

        products.MapPost("/{sku}/restock", async (string sku, RestockRequest request, CatalogDbContext db, CancellationToken ct) =>
        {
            if (request.Quantity <= 0)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["quantity"] = ["Quantity must be positive."] });

            var product = await db.Products.FirstOrDefaultAsync(p => p.Sku == sku, ct);
            if (product is null) return Results.NotFound();

            product.Restock(request.Quantity);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new ProductDto(product.Sku, product.Name, product.Price, product.OnHand, product.Reserved, product.Available));
        });
    }
}

internal static class CatalogSeeder
{
    public static async Task MigrateAndSeedAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        await db.Database.MigrateAsync();

        if (await db.Products.AnyAsync()) return;

        // Demo catalogue (fictional data).
        db.Products.AddRange(
            new Product("KB-MECH-01", "Mechanical Keyboard", 4499m, 50),
            new Product("MS-WL-02", "Wireless Mouse", 1299m, 120),
            new Product("MON-27-4K", "27\" 4K Monitor", 28999m, 15),
            new Product("HUB-USBC-7", "7-in-1 USB-C Hub", 2199m, 80),
            new Product("CAM-HD-1080", "1080p Webcam", 3499m, 40),
            new Product("SSD-NVME-1T", "1 TB NVMe SSD", 6999m, 3));
        await db.SaveChangesAsync();
    }
}
