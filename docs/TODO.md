# Findings: risks, gaps, open questions

Each item is labelled **Confirmed** (verified in code), **Potential risk** (real code path, impact depends on conditions not fully verifiable here), or **Uncertain** (needs a decision or information the repository doesn't contain). Trivial imperfections are omitted.

## Security

**Confirmed — order prices are client-supplied and never validated.**
`POST /orders` takes `UnitPrice` per line item (`OrderItemRequest`), stores it verbatim, and computes `TotalAmount` from it. Nothing consults CatalogService. A caller can order at any price they choose. The saga only checks stock, never price or product existence.
`src/Services/OrderService/Application/OrdersService.cs`, `Application/Orders/OrderItemRequest.cs`

**Confirmed — development JWT signing key and admin credentials are committed in `appsettings.json`.**
Every service's `appsettings.json` carries a placeholder `Jwt:Key`; UserService's also carries a placeholder `Admin:Email`/`Admin:Password`. Compose overrides all of them from the git-ignored `deploy/.env`, and the code comments label them dev-only — but they remain live fallbacks for any deployment that forgets to override, and the token key is symmetric (holding it means being able to mint admin tokens). Consider failing startup on the known placeholder value outside Development.

**Confirmed — `GET /users` exposes every user's email to any authenticated caller.**
No `Admin` requirement, no paging, no filtering. `src/Services/UserService/Program.cs`

**Confirmed — inventory levels are publicly readable.**
`GET /inventory` and `GET /inventory/{productId}` have no `RequireAuthorization`, unlike the write endpoints. Whether that is intentional is not stated anywhere. `src/Services/InventoryService/Program.cs`

**Confirmed — no account lockout; login rate limiting is now in place.**
`POST /users/login` is now rate-limited by IP: 10 requests per minute (sliding window, no queue). PBKDF2 at 100k iterations makes each attempt CPU-expensive so the limit is intentionally tight. No account-lockout mechanism exists — repeated failures from different IPs are not correlated.

**Confirmed — tokens cannot be revoked.**
A `jti` claim is minted but never persisted or checked, and there is no refresh token. Every token stays valid for its full hour. `JwtTokenGenerator`

## Distributed-system behaviour

**Resolved — the saga now has a timeout.**
`CheckoutTimeoutSweeper` cancels any checkout still waiting after `Checkout:Timeout` (default 5 min) and releases the stock the saga had recorded as reserved; the next item covers reservations still in flight. Covered by an integration test and verified on the running stack. How it works and its limits: [order-flow.md](order-flow.md). `order_pending_oldest_age_seconds` and the `OrderStuckPending` alert remain as the backstop for anything the sweeper cannot resolve.
`src/Services/OrderService/Infrastructure/CheckoutTimeoutSweeper.cs`, `Application/Sagas/OrderSagaStateMachine.cs`

**Resolved — a stock reservation that completes after the timeout is now released.**
The first version of the timeout finalized the saga, deleting its row, so a `ReserveStock` still queued at that moment (InventoryService down) took stock for a cancelled order and the resulting `StockReserved` was dropped silently. Reproduced on the docker-compose stack before the fix: order `Cancelled`, stock 23 → 22, no fault, no `_error` queue, no log line. The saga now moves to a `TimedOut` state and is kept for `Checkout:TimedOutRetention`, so a late `StockReserved` releases that reservation. Details, and `order_late_reservation_released_total`: [order-flow.md](order-flow.md).
*Residual:* a reservation that completes after the retention window (default 24 h) is still lost, silently. `TimedOutRetention` should exceed the longest InventoryService outage you intend to survive.
`src/Services/OrderService/Application/Sagas/OrderSagaStateMachine.cs`

**Resolved — a fast `StockReserved` could outrun the saga's own commit and be dropped, hanging the checkout.**
The saga published `ReserveStock` while its own transaction was still open and only then inserted and committed its row, so a quick InventoryService could answer first: `StockReserved` looked the saga up, found nothing, and was dropped without a fault. On the running stack this showed up as the first order after `order-api` restarted stalling until the timeout cancelled it, with that order's stock lost (22 → 21). Reproduced deterministically by `Checkout_WhenInventoryRepliesBeforeTheSagaCommits_StillConfirms`, which holds the saga's `INSERT` open with a table lock while InventoryService is healthy: before the fix the order ended `Cancelled`. Fixed by `UseEntityFrameworkOutbox<OrderDbContext>` on the saga endpoint, which holds the saga's publishes until its transaction commits. After: the test confirms the order, and four consecutive first-orders-after-restart on the running stack each confirmed in about a second.

**Resolved — a redelivered stock response was counted twice.**
The saga endpoint had no inbox, so an at-least-once redelivery of `StockReserved` incremented `ResponseCount` again. Reproduced: a two-item order was **confirmed after hearing about only one item**, with the second item never reserved. The same endpoint middleware includes the inbox, which deduplicates by `MessageId` and consumer. `Checkout_WhenAStockResponseIsRedelivered_DoesNotConfirmBeforeEveryItemHasAnswered` failed before (order `Confirmed`) and passes after (`Pending`).
*Log noise, not a failure:* when two copies of one message are processed at the same moment, the loser hits the inbox's unique constraint, so an `ERR` `DbUpdateException` (`23505 … AK_InboxState_MessageId_ConsumerId`) and one `WRN R-RETRY` are logged before the retry discards it. It is intermittent, because copies that arrive apart are caught by a plain lookup and log nothing.
`src/Services/OrderService/Program.cs`

**Unverified — does `FinalizeAsync` commit the order outside the saga transaction?**
This item originally read as confirmed (separate transactions, so a failure between them could leave a finalized, and via `SetCompletedWhenFinalized` deleted, saga with an order still `Pending`), but it was read from the code and never tested, and the evidence since points the other way. Instrumentation added during an earlier investigation (since removed) showed `FinalizeAsync` running on the same scoped `DbContext` as the saga repository with an ambient transaction open, and the saga endpoint now also carries the EF outbox, which puts the saga, the order repository and the outbox on one shared transaction. Whether the order write and the saga write now commit atomically has not been tested; settling it needs a failure-injection test (make the order's `SaveChangesAsync` fail and see whether the saga row survives).

**Potential risk — re-finalizing a resolved order faults the message.**
`Order.Confirm()`/`Cancel()` throw `InvalidOperationException` when status isn't `Pending`. Any path that reaches `FinalizeAsync` twice for the same order (see the two items above) produces a hard fault rather than an idempotent no-op. Making these transitions idempotent would be a small, low-risk change.

**Potential risk — nothing consumes the `_error` queue.**
No `IConsumeObserver`/fault consumer is registered, so a message that exhausts retry is parked with no automatic handling. Its *depth* is now visible (`rabbitmq_detailed_queue_messages_ready{queue=~".*_error"}`, with the `RabbitMqErrorQueueNotEmpty` alert), but there is no Alertmanager, so nothing notifies anyone and nothing replays the message.

**Potential risk — concurrent reservations rely on the inbox rather than optimistic concurrency.**
`InventoryItems` has no row-version or concurrency token, and reserving is a read-modify-write. Two `ReserveStock` messages for the same product processed concurrently (different orders) could each read the same `QuantityAvailable`. Whether MassTransit's per-endpoint concurrency limit prevents this in practice is **not verifiable from the configuration in the repo** — no `PrefetchCount`/`ConcurrentMessageLimit` is set, so the defaults apply.

## Operational

**Confirmed — no service applies EF migrations at startup.**
There is no `Database.Migrate()`/`EnsureCreated()` anywhere in `src/`. `docker compose up` produces empty databases and services that fail on first query; schema must be applied manually. This is the first thing to hit when bringing the stack up.

**Resolved — the saga integration test now runs in CI.**
`Saga.IntegrationTests` runs in a dedicated `integration-tests` job in `.github/workflows/ci.yml` on every push/PR to main. GitHub-hosted runners have Docker, so Testcontainers works without extra setup.

**Confirmed — Redis has no persistence volume and baskets have no TTL.**
Recreating the container discards all baskets; keys otherwise live forever. Fine if baskets are meant to be disposable, but that isn't stated anywhere.

**Potential risk — only OrderService can be pointed at a non-default RabbitMQ port.**
The other services call the `cfg.Host(host, "/", …)` overload, hardcoding 5672. `RabbitMq:Port` is read in OrderService only.

## Product gaps / consistency

**Confirmed — the basket is disconnected from checkout.**
BasketService neither publishes nor consumes anything, and nothing clears a basket when an order is created. The client must independently read the basket and post the order items. Either an integration event or an explicit client step is missing.

**Confirmed — no product↔inventory lifecycle link.**
Creating a product does not create an inventory record; `ReserveStock` for a product with no inventory row simply fails the order with "No inventory record for this product." Admins must remember to call `POST /inventory` separately.

**Confirmed — no way to create a second admin.**
The startup seeder is the only source of `Admin` users; `POST /users` always creates a `Customer`, and there is no promote/demote endpoint.

**Confirmed — order outcome is only discoverable by polling.**
`POST /orders` returns `201` while checkout is still running; there is no notification, callback or status-stream. Any client needs a polling loop (as the integration test has).

**Uncertain — is CatalogService's MediatR/CQRS style the intended target for the other services?**
Catalog uses MediatR; the other five use plain application-service classes. The repository gives no indication whether this is a migration in progress or a one-off. Worth settling before adding features to either style.

**Uncertain — where is the frontend?**
The task brief describes a Next.js/React Query/Zustand frontend, and the gateway's CORS default (`http://localhost:3100`) implies one exists, but **no frontend code is present in this repository.** It is either a separate repo or not yet written. Nothing about frontend architecture could be verified, so no `frontend.md` was written.
