using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Extensions.Hosting;

public static class MessagingExtensions
{
    /// <summary>
    /// The one messaging setup every service shares:
    /// RabbitMQ transport (connection string "messaging", injected by Aspire),
    /// kebab-case queue names, retries, and the EF Core transactional outbox on <typeparamref name="TDbContext"/>.
    /// <list type="bullet">
    /// <item>Bus outbox: messages published from an API request are stored in the same SQL transaction
    /// as the business data and delivered afterwards, so you never save without publishing (or the reverse).</item>
    /// <item>Consumer outbox / inbox: each consumed message is processed once, even if RabbitMQ redelivers it.</item>
    /// </list>
    /// </summary>
    public static IHostApplicationBuilder AddMessaging<TDbContext>(
        this IHostApplicationBuilder builder,
        Action<IBusRegistrationConfigurator> registerConsumers)
        where TDbContext : DbContext
    {
        builder.Services.AddMassTransit(x =>
        {
            x.SetKebabCaseEndpointNameFormatter();

            x.AddEntityFrameworkOutbox<TDbContext>(o =>
            {
                o.UseSqlServer();
                o.UseBusOutbox();
                o.QueryDelay = TimeSpan.FromSeconds(1);
                o.DuplicateDetectionWindow = TimeSpan.FromMinutes(30);
            });

            x.AddConfigureEndpointsCallback((context, _, endpoint) =>
            {
                // Transient faults (deadlocks, optimistic-concurrency conflicts) are retried in-process
                // before the message is moved to the _error queue.
                endpoint.UseMessageRetry(r => r.Intervals(100, 250, 500, 1000, 2000));
                endpoint.UseEntityFrameworkOutbox<TDbContext>(context);
            });

            registerConsumers(x);

            x.UsingRabbitMq((context, cfg) =>
            {
                cfg.Host(new Uri(builder.Configuration.GetConnectionString("messaging")
                    ?? throw new InvalidOperationException("Connection string 'messaging' is missing.")));
                cfg.ConfigureEndpoints(context);
            });
        });

        return builder;
    }
}
