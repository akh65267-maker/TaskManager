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

**Confirmed — the saga has no timeout, so a lost response strands the order permanently.**
No MassTransit scheduler is configured and the state machine defines no `Schedule`/timeout event. If a `ReserveStock` or a stock response is never delivered (for example after exhausting retries into the `_error` queue), the saga stays in `AwaitingStockReservation` forever: the order remains `Pending` and any stock already reserved for it is never released. This is the highest-impact correctness gap in the workflow.
`src/Services/OrderService/Application/Sagas/OrderSagaStateMachine.cs`
*Still unfixed, but no longer silent:* `order_pending_oldest_age_seconds` and the `OrderStuckPending` alert now detect it (see [observability.md](observability.md)). Detection is not a fix — the stock stays reserved until someone intervenes.

**Confirmed — the saga endpoint has no inbox, so a redelivered stock response is counted twice.**
InventoryService's consumers dedupe via the EF inbox; OrderService's saga endpoint is configured through `ConfigureEndpoints` with no `UseEntityFrameworkOutbox`. Delivery is at-least-once, so a redelivered `StockReserved` would increment `ResponseCount` again and could finalize a multi-item order before every item has genuinely responded — confirming an order whose remaining items were never reserved. The same duplicate would also re-append to `ReservedProductIdsJson`, causing a double `ReleaseStock` on the compensation path.

**Confirmed — `FinalizeAsync` commits the order outside the saga transaction.**
The saga state transition and `repo.SaveChangesAsync()` on the order are separate transactions. A failure between them can leave a finalized (and, via `SetCompletedWhenFinalized`, deleted) saga with an order still `Pending`. Retry helps only while the saga row still exists.

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
