using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Routes live in appsettings.json ("ReverseProxy"). Destinations such as http://orders are
// resolved through Aspire service discovery, so no host names or ports are hard-coded.
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
    .AddServiceDiscoveryDestinationResolver();

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("per-client", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = builder.Configuration.GetValue("RateLimit:PerMinute", 120),
            Window = TimeSpan.FromMinutes(1)
        }));
});

var app = builder.Build();

app.UseRateLimiter();

app.MapGet("/", () => Results.Ok(new
{
    service = "OrderFlow API gateway",
    routes = new[] { "GET/POST /api/orders", "GET /api/orders/{id}", "GET /api/products", "POST /api/products/{sku}/restock" }
}));

app.MapDefaultEndpoints();
app.MapReverseProxy();

app.Run();
