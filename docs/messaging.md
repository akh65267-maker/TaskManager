# Messaging

RabbitMQ via MassTransit. All contracts are `sealed record`s in `src/Shared/Contracts`, referenced by both sides — there is no schema registry and no versioning scheme.

## Contracts

| Message | Namespace folder | Shape |
|---|---|---|
| `OrderSubmitted` | IntegrationEvents | `OrderId, UserId, IReadOnlyCollection<OrderLineItem>` |
| `StockReserved` | IntegrationEvents | `OrderId, ProductId` |
| `StockReservationFailed` | IntegrationEvents | `OrderId, ProductId, Reason` |
| `UserRegistered` | IntegrationEvents | `UserId, Email, RegisteredAtUtc` |
| `ReserveStock` | Commands | `OrderId, ProductId, Quantity` |
| `ReleaseStock` | Commands | `OrderId, ProductId, Quantity` |
| `OrderLineItem` | Common | `ProductId, Quantity, UnitPrice` |

The Commands/IntegrationEvents split is naming only: **every message is sent with `Publish`**, never `Send`. Commands reach their consumer because InventoryService binds explicitly named receive endpoints (`ReserveStock`, `ReleaseStock`) to the published message types.

## Producers and consumers

| Message | Published by | Consumed by |
|---|---|---|
| `UserRegistered` | UserService (`UsersService.RegisterAsync`) | TaskService (`UserRegisteredConsumer`) — out of scope |
| `OrderSubmitted` | OrderService (`OrdersService.CreateAsync`) | OrderService saga (`Initially`) |
| `ReserveStock` | OrderService saga | InventoryService `ReserveStockConsumer` |
| `StockReserved` | InventoryService `ReserveStockConsumer` | OrderService saga |
| `StockReservationFailed` | InventoryService `ReserveStockConsumer` | OrderService saga |
| `ReleaseStock` | OrderService saga (compensation) | InventoryService `ReleaseStockConsumer` |

`ReleaseStockConsumer` publishes nothing — compensation is fire-and-forget from the saga's point of view.

## Outbox / inbox

| Service | Configuration | Effect |
|---|---|---|
| UserService | `AddEntityFrameworkOutbox<UserDbContext>` + `UseBusOutbox` | outbox only (no consumers) |
| OrderService | `AddEntityFrameworkOutbox<OrderDbContext>` + `UseBusOutbox`; endpoints via `ConfigureEndpoints` | outbox on publish; **no inbox on the saga endpoint** |
| InventoryService | `AddEntityFrameworkOutbox<InventoryDbContext>` (no `UseBusOutbox`) + `UseEntityFrameworkOutbox` per receive endpoint | inbox **and** outbox on both consumers |

Inbox/outbox tables are created by `AddInboxStateEntity()` / `AddOutboxStateEntity()` / `AddOutboxMessageEntity()` in `OrderDbContext` and `InventoryDbContext`.

**The load-bearing ordering rule, repeated in three places in the code:** `Publish` must be called *before* the `SaveChangesAsync` that commits the business change. The outbox only flushes buffered messages during a `SaveChanges` on the same DbContext; publishing afterwards leaves the message buffered with nothing to flush it, and it is silently lost. Both `ReserveStockConsumer` early-return paths still call `SaveChangesAsync` for the same reason — it is also what writes the inbox dedup entry.

## What the implementation actually guarantees

Verified:

- **At-least-once delivery.** Standard RabbitMQ + MassTransit redelivery; nothing suppresses duplicates at the broker.
- **Atomic "state change + message emitted"** where the outbox is used (UserService register, OrderService create order, both Inventory consumers): one DB transaction covers the row and the outbox record.
- **Consumer-side deduplication in InventoryService only**, by `MessageId + ConsumerId` via the EF inbox. This is what prevents a redelivered `ReserveStock` from double-decrementing stock.
- **Bounded retry then circuit breaking** on Inventory and Order (intervals and thresholds in [services.md](services.md)).

Not provided by this implementation — do not assume it:

- **No exactly-once processing.** Only Inventory's consumers dedupe; the OrderService saga endpoint has no inbox, so a redelivered `StockReserved`/`StockReservationFailed` would increment `ResponseCount` a second time (see [TODO.md](TODO.md)).
- **No distributed transaction.** The saga's final `Order.Confirm()/Cancel()` + `SaveChanges` is a separate transaction from the saga-state transaction.
- **No message ordering guarantee.** The saga is written to tolerate arbitrary arrival order of per-item responses, and `UseMessageRetry` on OrderService exists specifically because concurrent responses race on the same saga row.
- **No dead-letter/fault handling of its own.** No `ReceiveEndpoint` fault consumers, no `_error` queue monitoring, no `IConsumeObserver`. Messages that exhaust retries go to MassTransit's default `_error` queue and are not surfaced anywhere.
- **No scheduling/timeouts.** No `MassTransit` scheduler is configured, so the saga has no timeout (see [order-flow.md](order-flow.md)).

## Connection configuration

`RabbitMq:Host` / `RabbitMq:Username` / `RabbitMq:Password`, defaulting to `localhost` and the RabbitMQ guest account when unset. OrderService additionally reads `RabbitMq:Port` (default 5672) — the other services hardcode the default port in their `cfg.Host(...)` overload, which is why the integration test can only redirect OrderService to an arbitrary Testcontainers port and must configure Inventory's bus itself.
