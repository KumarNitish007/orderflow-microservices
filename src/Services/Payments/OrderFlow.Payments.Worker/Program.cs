using Microsoft.EntityFrameworkCore;
using OrderFlow.Payments.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddSqlServerDbContext<PaymentsDbContext>("paymentsdb", s => s.DisableRetry = true);
builder.Services.AddSingleton<IPaymentGateway, SimulatedPaymentGateway>();
builder.AddMessaging<PaymentsDbContext>(x => x.AddConsumer<ProcessPaymentConsumer>());

var host = builder.Build();

await using (var scope = host.Services.CreateAsyncScope())
    await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().Database.MigrateAsync();

host.Run();
