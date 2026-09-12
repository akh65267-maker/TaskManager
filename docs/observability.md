# Observability

Three signals, three backends, one id tying them together.

| Signal | Produced by | Shipped how | Viewed in |
|---|---|---|---|
| Logs | Serilog → console | container stdout | `docker compose logs` (no log aggregator — Loki is deliberately out of scope) |
| Traces | OpenTelemetry | **push**, OTLP/gRPC | Jaeger, `http://localhost:16686` |
| Metrics | OpenTelemetry | **pull**, Prometheus scrape of `GET /metrics` | Prometheus `:9090`, Grafana `:3000` |

Every log line carries the trace id, and the trace id is the correlation id (see [decisions.md](decisions.md)). Grafana has both Prometheus and Jaeger as datasources so a latency panel can link into the trace behind it.

## Where it is configured

| Concern | File |
|---|---|
| All three signals, for every service | `src/Shared/Observability/ObservabilityExtensions.cs` |
| Scrape targets, alert rule loading | `deploy/prometheus/prometheus.yml` |
| Alert rules | `deploy/prometheus/rules/checkout.yml` |
| Grafana datasources + dashboard provider | `deploy/grafana/provisioning/` |
| Dashboards (committed JSON) | `deploy/grafana/dashboards/` |
| RabbitMQ metrics plugin | `deploy/rabbitmq/enabled_plugins` |

`GET /metrics` is mapped by an `IStartupFilter` inside `AddObservability`, so no service's `Program.cs` mentions it. Adding the endpoint to a new service is automatic.

Dashboards and datasources are **provisioned from the repository**, not created in the UI (`allowUiUpdates: false`). Edits made in Grafana's UI will not persist.

## Metrics that exist

Built-in, no instrumentation written:

| Metric (Prometheus name) | From |
|---|---|
| `http_server_request_duration_seconds{_count,_bucket}` | ASP.NET Core meter — request rate, error rate by `http_response_status_code`, latency by `http_route` |
| `http_client_request_duration_seconds*` | HttpClient meter (near-zero traffic: there are no service-to-service HTTP calls) |
| `messaging_masstransit_*` | the `MassTransit` meter — consume/publish counts and durations. Registered but not yet used in a dashboard; browse the exact instrument names in Prometheus once the stack is up. |
| `rabbitmq_*`, `rabbitmq_detailed_queue_*` | `rabbitmq_prometheus` plugin |

Application metrics (`OrderService/Application/OrderMetrics.cs`, meter `TaskManager.Orders`):

| Metric | Type | What it answers |
|---|---|---|
| `order_pending_oldest_age_seconds` | gauge | **Is a checkout stranded?** The saga has no timeout, so this is the only signal that one is. Near zero when healthy. |
| `order_saga_active` | gauge | How many checkouts are in flight |
| `order_outbox_backlog` | gauge | Is the outbox draining into RabbitMQ |
| `order_saga_finalized_total{outcome}` | counter | Confirmed vs cancelled rate |
| `order_stock_reservation_failed_total` | counter | How often stock reservation is refused |

The three gauges are **read from the database** by `OrderMetricsCollector` (a `BackgroundService`, 15 s interval) and cached; the gauge callbacks only read the cached value, because a Prometheus scrape must not run queries on the collection thread. They describe persisted state, so they are correct after a restart and across multiple instances — an in-memory counter would not be.

`order_stock_reservation_failed_total` is deliberately **unlabelled**: the failure reason reaching the saga is an exception message containing the requested and available quantities, so using it as a label would create a new time series per failure. The reason stays in logs and traces.

## Alerts

Prometheus evaluates `deploy/prometheus/rules/checkout.yml`. **There is no Alertmanager** — firing alerts are visible only on Prometheus' Alerts page and in Grafana; nothing notifies anyone. Treat them as documented thresholds, not as paging.

| Alert | Condition |
|---|---|
| `OrderStuckPending` | an order has been `Pending` > 5 min |
| `RabbitMqErrorQueueNotEmpty` | any `*_error` queue is non-empty (messages that exhausted retry; nothing consumes them) |
| `OutboxBacklogGrowing` | > 50 outbox rows for 5 min |

## Known limits

- `/metrics` is **unauthenticated and published on every service's host port** (8080–8085). Fine for local compose; it must not be reachable publicly in a real deployment — bind it to an internal interface or put auth in front.
- **TaskService is not scraped.** It serves `/metrics` (it shares `AddObservability`) but is intentionally outside the scope of this documentation, so no target was added for it.
- The `service` label on service metrics comes from the **scrape config**, not from the OTel resource; resource attributes land in `target_info`. A new service needs a target entry in `prometheus.yml` or it is simply not scraped.
- MassTransit dashboards are not built yet; the meter is registered but the panels are not.
- Four containers grew to six. `prometheus` and `grafana` start with the stack, like Jaeger.
- The Prometheus exporter package is upstream's permanently-`-beta` release (its stability is gated on the Prometheus exposition format, not on the SDK), pinned to `1.18.0-beta.1` to match the stable OTel packages.
