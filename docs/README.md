# Documentation index

Compact, verified knowledge about this repository. **Source code is the ultimate source of truth** — if a document disagrees with the code, the code wins and the document should be corrected.

| Read this | For |
|---|---|
| [architecture.md](architecture.md) | System shape, gateway routing, ports, observability, build/CI |
| [services.md](services.md) | What each service owns, endpoints, storage, resilience |
| [messaging.md](messaging.md) | RabbitMQ/MassTransit contracts, producers/consumers, outbox/inbox, delivery guarantees |
| [order-flow.md](order-flow.md) | The checkout saga: success, failure, compensation |
| [authentication.md](authentication.md) | JWT issuance/validation, roles, admin access |
| [database.md](database.md) | Database-per-service boundaries, schemas, migrations |
| [observability.md](observability.md) | Logs/traces/metrics, the metrics that exist, dashboards, alerts, limits |
| [decisions.md](decisions.md) | Architectural decisions evident from the implementation |
| [TODO.md](TODO.md) | Confirmed issues, potential risks, open questions |
| [platform-plan.md](platform-plan.md) | Decisions on rate limiting, refresh tokens, CI/CD, dev tooling — phase 1 (metrics/Prometheus/Grafana) is implemented, the rest is not |

Notes on scope:

- `TaskService` is intentionally excluded from analysis. It exists in the repo and in the gateway/compose topology, and it consumes `UserRegistered`; nothing else about it is documented here.
- There is **no frontend in this repository** (see [TODO.md](TODO.md)). The Next.js storefront lives in the separate `taskmanager-web` repo and is documented there in `docs/frontend.md`; it talks to this system only through the API gateway (`/users`, `/products`, `/inventory`, `/orders`).
