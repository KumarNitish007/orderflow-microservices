# Design decisions

## 1. Orchestration saga, not choreography

The order workflow has a clear owner (the Orders service) and an order of steps that must not be skipped:
reserve, then charge, then complete or compensate. A **state machine saga** keeps that logic in one readable place
(`OrderStateMachine.cs`), persists progress, and makes the current state of any order a simple query.

With pure choreography each service reacts to the others' events and the workflow exists only implicitly.
That's fine for two steps, but compensation paths get hard to reason about. Services here still publish events
(`StockReserved`, `PaymentFailed`...) so other consumers can subscribe without the saga changing.

## 2. Commands are sent, events are published

`ReserveStock`, `ProcessPayment` and `ReleaseStock` are **sent** to the single queue that owns the work
(`queue:reserve-stock`...). Outcomes are **published** as events anyone may consume. The split makes ownership explicit,
and a misspelt queue name fails tests instead of silently dropping messages: the in-process workflow tests wire the
real consumers to the real saga.

## 3. Transactional outbox + inbox everywhere

Without an outbox, "save the order, then publish the event" has two failure windows: saved but not published
(the order is stuck forever), or published but not saved (a phantom order). The MassTransit EF Core outbox writes
outgoing messages into `OutboxMessage` in the **same transaction** as the business change, and a delivery service
forwards them after commit. On the consuming side the **inbox** records processed message ids, so RabbitMQ's
at-least-once delivery becomes effectively once.

The handlers stay idempotent at the business level as well: reservations and payments are keyed by OrderId,
and a unique index on `payments.Payments(OrderId)` makes a double charge impossible even if every other safeguard failed.

## 4. Stock as OnHand + Reserved

A reservation holds units (`Reserved += n`) without removing them. Payment success commits them (`OnHand -= n`),
and failure releases them. That keeps physical stock accurate while orders are in flight and makes compensation a simple
inverse operation. Check constraints in the database (`Reserved <= OnHand`, both non-negative) back up the domain rules.
Concurrent reservations of the same product are caught by a concurrency token and retried by the consumer retry policy.

## 5. Synchronous price lookup

Orders calls Catalog over HTTP for prices when an order is placed. The client needs an immediate yes/no
(unknown SKU = 400), and prices must be authoritative. The call goes through service discovery and the standard
resilience pipeline (timeout, retry, circuit breaker). Everything after acceptance is asynchronous, so a slow payment
provider never blocks checkout; the client gets `202 Accepted` and polls the order.

## 6. Database per service, schema per service

Each service owns its database (`catalogdb`, `ordersdb`, `paymentsdb`) and a SQL schema. No service reads another's tables.
In production these could live on one server for cost and be split later without code changes.
Schema changes are EF Core migrations applied at startup for the demo, and each migration set is also exported as an
**idempotent SQL script** in `/database` for review or for DBA-run deployments.

## 7. MassTransit v8

MassTransit v9 moved to a commercial licence. v8 (Apache-2.0) is used deliberately and its APIs are the same for
everything here. Migrating later is a package bump plus a licence key.

## 8. Aspire for local orchestration and testing

The AppHost is the single description of the system: containers, databases, queues, services and how they reference each other.
Connection strings and container passwords are injected at runtime (generated and kept in user-secrets), so no credentials
are in the repository. The same app model powers the end-to-end tests (`Aspire.Hosting.Testing`), so CI exercises the
real topology rather than mocks.

## 9. What I'd add for production

- Gateway authentication (JWT/OIDC) and service-to-service auth
- Payment and reservation timeouts using scheduled messages
- Dead-letter monitoring and replay tooling for `_error` queues
- Migration bundles run by the deployment pipeline instead of at startup
- Horizontal scaling: consumers are stateless and the saga uses optimistic concurrency, so it scales by adding instances
