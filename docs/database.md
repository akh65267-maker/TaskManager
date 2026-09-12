# Data & persistence boundaries

Database-per-service, enforced physically: `deploy/docker-compose.yml` runs a **separate Postgres container per service**. No service can read another's data, and there are no cross-service foreign keys.

| Service | Store | Connection string key | Compose DB / container | Host port |
|---|---|---|---|---|
| TaskService | Postgres 17 | `TaskDatabase` | `taskflow` / `postgres` | 5432 |
| UserService | Postgres 17 | `UserDatabase` | `userflow` / `postgres-users` | 5433 |
| CatalogService | Postgres 17 | `CatalogDatabase` | `catalogflow` / `postgres-catalog` | 5434 |
| InventoryService | Postgres 17 | `InventoryDatabase` | `inventoryflow` / `postgres-inventory` | 5435 |
| OrderService | Postgres 17 | `OrderDatabase` | `orderflow` / `postgres-order` | 5436 |
| BasketService | Redis 7 | `Redis` | `redis` | 6379 |

Each Postgres service has its own named volume; Redis has **none** — basket data is lost when the container is recreated.

## Ownership boundaries that matter

- `ProductId` is the join key between Catalog (product definition + price) and Inventory (stock), but it is **only a convention**. Creating a product does not create an inventory record, and `ReserveStock` for an unknown product is answered with `StockReservationFailed("No inventory record for this product.")` rather than an error.
- Order line items store their **own copy of `UnitPrice`**, supplied by the client at checkout. Orders are therefore immune to later catalog price changes, but also unvalidated against the catalog (see [TODO.md](TODO.md)).
- `UserId` appears in Basket, Order and the saga, with no foreign key to UserService. Deleting a user (no endpoint exists) would orphan those rows.

## Schemas

**OrderDbContext** (`orderflow`)
- `Orders` — PK `Id`; `Status` persisted as a `string` (max 20) via `HasConversion<string>`; `TotalAmount` is `Ignore`d (computed); `Items` mapped as `OwnsMany` with a shadow `Guid Id` PK and shadow FK `OrderId`; `UnitPrice` `HasPrecision(18,2)`.
- `OrderSagaStates` — PK `CorrelationId`; `RowVersion` as `IsRowVersion()`; `CurrentState` max 64; `ItemsJson`/`ReservedProductIdsJson` required.
- MassTransit `InboxState`, `OutboxState`, `OutboxMessage`.

**InventoryDbContext** (`inventoryflow`)
- `InventoryItems` — **PK is `ProductId`** (no surrogate key), plus required `QuantityAvailable`. No reserved-quantity column and no row-version / concurrency token; safety under concurrent reservation relies on the inbox and message retry, not optimistic concurrency.
- MassTransit `InboxState`, `OutboxState`, `OutboxMessage`.

**UserDbContext** (`userflow`) — users plus the MassTransit outbox tables (outbox only; no consumers).

**CatalogDbContext** (`catalogflow`) — products only; no MassTransit tables (the service has no bus).

**Redis (Basket)** — one string key per user, `basket:{userId}`, holding the whole `Basket` object as JSON. No TTL, no secondary indexes.

## Migrations

EF Core migrations are committed for Catalog, Inventory, Order, User and Task under each service's `Migrations/` folder.

**No service applies migrations at startup** — there is no `Database.Migrate()` or `EnsureCreated()` call anywhere in `src/`. Schema must be applied out of band (`dotnet ef database update`, or an equivalent step); `docker compose up` alone leaves the databases empty and services failing on first query. `Saga.IntegrationTests` calls `MigrateAsync()` itself, and does so through a standalone `DbContext` rather than the test host's provider — touching `WebApplicationFactory.Services` starts the MassTransit outbox poller, which then fails against tables that don't exist yet.
