# Findings: risks, gaps, open questions

Each item is labelled **Confirmed** (verified in code), **Potential risk** (real code path, impact depends on conditions not fully verifiable here), or **Uncertain** (needs a decision or information the repository doesn't contain). Trivial imperfections are omitted.

## Security

**Resolved — order prices are now checked against the catalog.**
`POST /orders` used to store the caller's `UnitPrice` verbatim, so an order could be placed at any price. `OrdersService` now asks CatalogService for the real price of every product (`CatalogClient` → `GET /products/{id}`) and refuses the order, storing and publishing nothing, if a product does not exist (`422`), the price differs (`409`, so a changed price is never charged silently) or the catalog cannot be reached (`503`, no retry, 5 s timeout, so a price is never guessed). Covered by unit tests (including the catalog client against a stub HTTP handler), two saga integration tests that go through the real HTTP pipeline, and verified on the running stack: wrong price → 409, unknown product → 422, catalog stopped → 503, then 201 once it was back.
*Remaining:* (1) **It adds a synchronous dependency**: OrderService cannot take orders while CatalogService is down, where before only browsing was affected. (2) **A stale cart is refused, not repaired**: the frontend shows the server's message but does not refresh the cart's prices. There is no price-edit endpoint today, so a mismatch can only come from tampering or direct database edits; once one exists the cart needs a "prices changed, review" step. (3) The price is read just before the order is saved, not inside its transaction, so a price changed in that instant is accepted at the old price (the order records what the customer was shown, which is the point). (4) `Catalog:BaseUrl` is a new required setting: set in `deploy/docker-compose.yml` and `infra/main.bicep`, not yet exercised on Azure.
`src/Services/OrderService/Application/OrdersService.cs`, `Infrastructure/Catalog/CatalogClient.cs`

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

**Resolved — concurrent reservations of one product mostly failed, and the failures were silent.**
This item used to say the risk of concurrent reads was "not verifiable". The load tests verified it, and it was worse than a lost update. `ReserveStock` is a read-modify-write on one `InventoryItems` row, run in a MassTransit outbox transaction that defaults to SERIALIZABLE, where Postgres rejects the second writer with `40001 could not serialize access due to concurrent update`. Retries (100/500/1000/5000 ms, all on the same schedule) collided again, so roughly one reservation per product won per round and the rest exhausted their retries into `ReserveStock_error`, which nothing reads. Reproduced deterministically: **40 reservations for one product arriving together, 37 failed**, 3 runs out of 3. It showed up in practice as a broker outage (the backlog arrives at once) leaving the next checkouts cancelled, with a circuit breaker then open for 5 minutes.
Fixed in two parts that only work together: the inventory outbox now runs at `READ COMMITTED` (`InventoryOutbox`), so a second writer waits for the lock instead of failing, and every read-modify-write in a consumer takes the row lock first (`GetByProductIdForUpdateAsync`, `SELECT … FOR UPDATE`). **Neither is safe alone:** with READ COMMITTED and no lock, the same tests fail silently, with 38 of 40 decrements lost and stock overselling. After: all 40 applied in about half a second with no `40001`; demand beyond stock sells exactly the stock, never more; reservations and releases racing on one product leave the stock exactly where it started. It also works across several InventoryService replicas, since the lock is in the database.
`src/Services/InventoryService/Infrastructure/Messaging/InventoryOutbox.cs`, `Infrastructure/Persistence/InventoryRepository.cs`

**Resolved — restocking could lose a concurrent reservation.**
`POST /inventory/{id}/restock` read the quantity, added to it and wrote it back with no transaction, so the row lock the consumers take would have been released the instant its `SELECT` finished. A reservation that committed between the restock's read and its write was overwritten: stock went back up by the restock plus whatever that reservation had taken, while the order that reserved it stayed confirmed. Reproduced deterministically by `Restock_WhileAReservationIsInFlight_DoesNotOverwriteIt` (a reservation held open with the row lock, a restock of 5 started meanwhile, then the reservation commits): with 10 in stock and 4 reserved the stock ended at **15** instead of 11. Fixed by running the restock as a locked read-modify-write inside a transaction (`InTransactionAsync` + `GetByProductIdForUpdateAsync`), so it queues behind in-flight reservations and releases like every other writer; the test then passes and the three reservation concurrency tests still do. Not exercised through the HTTP endpoint on the running stack (that needs an admin token); the test drives the same service and repository against a real Postgres.
`src/Services/InventoryService/Application/InventoryItemsService.cs`

**Potential risk — the saga's own transactions are still SERIALIZABLE, and they do conflict.**
The same default applies to OrderService's saga endpoint. It is observed, not hypothetical: the integration suite logs `40001` from `order-service` (on `UPDATE "InboxState"`) in the tests that deliver several stock responses for one order. In those small tests every conflict was retried and succeeded, and a conflict there is narrower than the inventory one (per order, not per product), so it has **not** been shown to fail. But it is the same mechanism that failed 37 of 40 in InventoryService, with a different hot spot, and the saga has not been load-tested for it: a multi-item order whose responses arrive together is the case to try.

*Update (stress run, ~600 iterations/s):* the saga did log 309 `40001` conflicts under overload, all absorbed by retry; every one of the 7,756 orders still reached Confirmed and stock was conserved. So it costs throughput under overload but has not been shown to lose or corrupt anything. Switching the saga endpoint to READ COMMITTED (as done for inventory) is not done and would need its own analysis.

**Found (capacity limit, not corruption) — the order database runs out of connections under heavy load.**
In the stress run, at ~600 iterations/s (~1,150 requests/s) Postgres for OrderService hit `max_connections` ("too many clients already"). Overload was invisible at HTTP level (0% request errors) but showed as stalled async checkouts (about half resolved during the step) and a backlog that took over 150 s to drain; afterwards all invariants held. Not addressed. Options, none implemented: cap each service's connection pool, put PgBouncer in front of Postgres, raise `max_connections` (more memory), or shed load at the gateway before the saga backs up. The stress report now fails a step on unresolved checkouts, not just HTTP errors.

**Confirmed (behaviour, not a fault) — after a broker outage, checkouts resume 25–30 s after the broker is back.**
Measured by the `rabbitmq-down` chaos test. A service that lost RabbitMQ reconnects on a growing backoff: after a 45 s outage InventoryService logged failed attempts at 13, 17 and 19 s apart, the last one just after the broker returned but while it was still booting, so the next attempt landed about 25–30 s after the restore. During that gap checkouts placed after the restore wait (up to ~12 s observed, then the saga timeout is 30 s in tests and 5 min by default). No data is lost: every invariant held across the outage, with the outbox holding the messages. A longer outage means a longer backoff, so the gap grows with it; it has not been measured beyond 45 s. The reconnect schedule is MassTransit's default and is not configured anywhere in this repo.

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
