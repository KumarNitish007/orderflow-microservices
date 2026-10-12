# Database scripts

One **idempotent** SQL Server script per service, generated from its EF Core migrations
(`dotnet ef migrations script --idempotent`). Each can be run repeatedly; already-applied migrations are skipped.

| Script | Database / schema | Tables |
|---|---|---|
| `catalog.sql` | `catalogdb` / `catalog` | Products, Reservations, ReservationLines + outbox/inbox |
| `orders.sql` | `ordersdb` / `orders` | Orders, OrderItems, OrderStates (saga) + outbox/inbox |
| `payments.sql` | `paymentsdb` / `payments` | Payments (unique per order) + outbox/inbox |

`InboxState`, `OutboxState` and `OutboxMessage` are MassTransit's transactional outbox tables.

In local development the services apply their migrations on startup, so you don't need to run these by hand.
To regenerate after a model change:

```bash
dotnet tool restore
dotnet ef migrations add <Name> --project src/Services/Orders/OrderFlow.Orders.Api --output-dir Data/Migrations
dotnet ef migrations script --idempotent --project src/Services/Orders/OrderFlow.Orders.Api -o database/orders.sql
```

No server names, credentials or data are stored here. Connection strings are injected at runtime by the Aspire AppHost.
