# Architectural decisions

Decisions that are clearly evident from the implementation. Where the code carries an explanatory comment, that comment is the stated rationale; otherwise the historical motivation is not recorded in the repository and is marked as such.

---
**Database per service, physically separated.**
Evidence: five separate Postgres containers in `deploy/docker-compose.yml`, one connection-string key per service, no cross-service FKs, no shared DbContext.
Rationale: not stated in the repository. Consistent with keeping bounded contexts independent.

---
**Asynchronous messaging (RabbitMQ + MassTransit) as the only inter-service channel.**
Evidence: no `HttpClient`, service discovery or typed client targets another service anywhere in `src/`. All cross-service interaction is via the six contracts in `src/Shared/Contracts`.
Rationale: not stated. The effect is that checkout does not fail when InventoryService is momentarily down — the message waits.

---
**Shared contracts assembly rather than per-service duplicated DTOs.**
Evidence: `src/Shared/Contracts` referenced by publisher and consumer alike; `sealed record` types.
Trade-off visible in the code: consumers and producers are compile-time coupled to one contract version, and there is no versioning or upcasting scheme.

---
**OrderService owns the checkout saga.**
Evidence: `OrderSagaStateMachine` + `OrderSagaState` live in OrderService, persisted by MassTransit's EF Core saga repository into `OrderDbContext` — the same DbContext as `Orders`. The XML comment on `OrderSagaState` states the reason: "OrderService is the natural coordinator of its own checkout process."
Consequence: the saga can update the order row directly (`FinalizeAsync` resolves `IOrderRepository`) instead of round-tripping another message.

---
**Saga state stored as JSON blobs (`ItemsJson`, `ReservedProductIdsJson`) instead of owned EF collections.**
Evidence: `string` properties + `JsonSerializer` in the state machine.
Rationale (stated in the code comment): keeps the saga entity mapping simple. Cost: the fields are not queryable, and every transition deserializes/reserializes.

---
**Transactional outbox wherever a state change must emit a message.**
Evidence: `AddEntityFrameworkOutbox` in User, Order and Inventory; `UseBusOutbox` in User and Order; a single `SaveChangesAsync` after `Publish` in `UsersService.RegisterAsync`, `OrdersService.CreateAsync` and both Inventory consumers.
Rationale (stated in comments): the message and the row commit in one transaction, so a message can never be published for a change that rolled back, or lost for one that committed. The comments also record the sharp edge — `Publish` must precede `SaveChangesAsync`, or the message is silently dropped.

---
**Idempotency via the EF Core inbox, on InventoryService's consumers only.**
Evidence: `cfg.ReceiveEndpoint("ReserveStock" / "ReleaseStock", e => e.UseEntityFrameworkOutbox<InventoryDbContext>(context))` — explicit endpoints chosen specifically so the inbox can be attached, with endpoint names kept identical to what the convention produced.
Rationale (stated in the comment): deduplication by `MessageId + ConsumerId` closes the gap where a redelivered `ReserveStock` would double-reserve stock. Stock mutation is the one non-idempotent operation in the system; the saga's own endpoint was left convention-based and has no inbox.

---
**Message-level retry instead of EF `EnableRetryOnFailure` in the bus-backed services.**
Evidence: explicit comments in User/Inventory/Order `Program.cs` declining `EnableRetryOnFailure`, plus `UseMessageRetry` on each bus. CatalogService — which has no bus and no explicit transactions — *does* use `EnableRetryOnFailure(5, 10s)`.
Rationale (stated): EF's retrying execution strategy is incompatible with the user-initiated transactions that the saga repository and inbox/outbox open. OrderService's shorter intervals (100/250/500/1000 ms) are explicitly sized for Postgres serialization conflicts (`40001`) when two stock responses race on the same saga row.

---
**Circuit breaker on both bus-consuming services.**
Evidence: identical `UseCircuitBreaker` settings (tracking 1 min, trip 15, active threshold 10, reset 5 min) in Inventory and Order.
Rationale (stated): stop hammering a dependency that is genuinely down, as distinct from the momentary conflicts retry handles.

---
**YARP gateway as a pure path-prefix router; authentication delegated to each service.**
Evidence: `ApiGateway/Program.cs` contains only `AddReverseProxy().LoadFromConfig`, CORS and request logging — no `AddAuthentication`. Every service configures its own `AddJwtBearer` with the same `Jwt:*` settings.
Rationale: not stated. Effect: services are independently secure even if reached directly (their ports are published in compose), at the cost of duplicated validation config in six places and a single shared symmetric key.

---
**Symmetric-key (HS256) JWTs with a shared secret, rather than asymmetric signing.**
Evidence: `SymmetricSecurityKey` from `Jwt:Key` in both the issuer and all validators.
Consequence, evident from the code: every validating service holds the *signing* key, so any of them could mint tokens. Historical rationale unknown.

---
**Redis (not Postgres) for baskets, as one JSON blob per user.**
Evidence: `RedisBasketRepository`, key `basket:{userId}`, no EF, no migrations, `AbortOnConnectFail = false` so startup survives Redis not being ready.
Rationale: not stated. The `Basket` XML comment records only the shape ("no relational store here"). Note that no TTL is set and the Redis container has no volume, so baskets are treated as disposable in practice but not by explicit policy.

---
**MediatR/CQRS in CatalogService only.**
Evidence: `AddMediatR` and `CreateProductCommand` / `GetProductByIdQuery` / `ProductListQuery` handlers in CatalogService; every other service uses a plain injected application-service class.
Rationale: unknown from the repository. Whether Catalog is the pattern the other services are expected to converge on, or an experiment that was not repeated, is **unclear from the current implementation.**

---
**OpenTelemetry trace id reused as the correlation id.**
Evidence: `ObservabilityExtensions` XML comment states it explicitly — the trace id is generated per request and propagates onto messages via MassTransit's native `ActivitySource`, "without a second, hand-rolled correlation header." Serilog's console template prints `{TraceId}` on every line.

---
**OTLP endpoint left to environment variables.**
Evidence: `tracing.AddOtlpExporter()` with no endpoint argument; `OTEL_EXPORTER_OTLP_ENDPOINT`/`_PROTOCOL` set per service in compose.
Rationale (stated): so each environment configures its own collector independently.

---
**Unit tests in CI; the saga integration test excluded.**
Evidence: `.github/workflows/ci.yml` runs the six unit-test projects one by one.
Rationale (stated in the workflow comment): `Saga.IntegrationTests` needs Testcontainers/Docker and showed timing-sensitive flakiness locally, deferred to a dedicated workflow that does not exist yet.
