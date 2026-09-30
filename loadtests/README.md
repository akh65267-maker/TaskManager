# Load, stress, spike, soak and resilience tests

Performance tests for the whole stack, written for [k6](https://k6.io), run from a Docker image (nothing to install), and checked afterwards against the databases and the broker.

| Profile | The question it answers | Shape of the load |
|---|---|---|
| `load` | Can the system handle the traffic we expect? | A steady number of concurrent shoppers |
| `stress` | What happens when we exceed its capacity? | Arrival rate stepped up until something gives, then back down |
| `spike` | What happens if traffic suddenly jumps? | Calm, then 10x within 10 seconds, then calm again |
| `soak` | What happens after running under load for hours? | Steady moderate load for a long time |
| `chaos` | What happens when a dependency fails? | Steady load while a dependency is stopped or frozen, then restored |

```bash
loadtests/run.sh load                                  # 20 shoppers, 3 minutes steady
loadtests/run.sh stress
loadtests/run.sh spike
loadtests/run.sh soak --minutes 120
loadtests/run.sh chaos --fault inventory-down
loadtests/run.sh --list                                # profiles and faults
loadtests/scripts/cleanup.sh                           # remove the data the runs created
```

Run these from WSL or Linux (`docker compose up -d` in `deploy/` first). They only ever target the **local** stack; see [Testing a deployed environment](#testing-a-deployed-environment).

## What a run does

1. Applies a **30-second checkout timeout** to `order-api` (default is 5 minutes) and restores it afterwards. Without this, no run can be judged until five minutes after the load stops.
2. Starts k6 in a container on the compose network. Its setup registers a few throwaway users, logs each in once, and (given admin credentials from `deploy/.env`) creates a dedicated `loadtest-...` product with a million units of stock.
3. Generates traffic: mostly browsing, some basket use, some checkouts. A checkout is timed from placing the order until it leaves `Pending`, because the saga makes it asynchronous and the HTTP latency of `POST /orders` alone says nothing about it.
4. For `chaos`, breaks a dependency on a timeline, then restores it.
5. Waits for the system to settle and checks the **invariants** below.

Results go to `loadtests/results/<profile>-<timestamp>/` (git-ignored): `k6.log`, the k6 JSON, `invariants.txt`, and for soaks a memory sample.

Exit code: `0` all good, `1` a k6 threshold failed, `2` an invariant was violated, `3` the run could not be carried out.

## The invariants

Latency and error rate say how the system felt. They say nothing about whether it stayed *correct*, and correctness is what overload and failure usually damage. After every run `scripts/invariants.sh` reads the databases and RabbitMQ directly (not through the API that was just being abused) and checks:

| Invariant | What violating it means |
|---|---|
| No order left `Pending` | An order was abandoned: neither confirmed nor cancelled |
| No saga still awaiting stock | The saga and its timeout both failed to finish the checkout |
| **Stock conserved**: `initial - sold = final` | Positive difference: stock **vanished** (a reservation nothing released). Negative: stock was **created** (something released twice) |
| Outbox drained | A committed message never reached the broker |
| No messages in `_error` queues | A message exhausted its retries and nothing consumes those queues, so it would otherwise be silent |

The system is eventually consistent, so the check keeps retrying for up to 150 seconds (`SETTLE_WAIT_S`) before declaring a failure. One that clears was just still in flight; one that persists is real.

## Reading the results

- **Thresholds are for this machine, not for production.** The defaults (`P95_BROWSE_MS=500` and so on, in `k6/lib/config.js`) describe a laptop running the whole stack *and* the load generator, sharing CPU. Use a run to compare before and after a change, or to find where something breaks relative to itself. Do not quote its numbers as capacity. Set thresholds from real traffic and SLOs before using a run to make a capacity claim.
- **`load`**: any failure means the system cannot carry expected load. What "expected" is, is yours to say: set `VUS` from real numbers (concurrent users is roughly requests per second times average session think-time).
- **`stress`**: read the by-step table. The report names the highest step that held (<2% failed, p95 <1s) and the first that did not. The arrival-rate executor keeps starting iterations whether or not the system keeps up, which is what real overload looks like; watch `DROPPED` iterations too. Then look at the invariants: a system that fails *cleanly* under overload and comes back is fine; one that corrupts data is not.
- **`spike`**: the pass criteria apply to the baseline and the recovery, not the spike. Failing during a 10x jump is expected; not returning to normal afterwards is the finding.
- **`soak`**: the report compares p95 in the first tenth of the run with the last tenth (`LOADTEST_VERDICT soak-latency-drift`) and lists memory per container, first vs last tenth. Memory growth is a **warning**, not a failure: a JIT or a cache warming up looks like a leak until you run longer. Use `--minutes 120` or more for a real soak.
- **`chaos`**: the fault phase is deliberately unjudged (errors are expected while a dependency is down). Read *which* endpoints failed and how, and check that the baseline and recovery phases pass and the invariants hold.

### Faults (`--fault`)

Two failure shapes behave differently. **down** stops the container, so connections are refused immediately. **hang** freezes it (`docker pause`): connections stay open and nothing answers, which is what a saturated or partitioned service looks like and is the case that exposes missing timeouts.

`inventory-down` `inventory-hang` `catalog-down` `basket-down` `rabbitmq-down` `rabbitmq-hang` `order-db-down` `order-db-hang` `inventory-db-down` `redis-down` `order-restart` (a cold restart under load) `gateway-down`

## Settings

Pass with `--env NAME=VALUE` (or export them). Defaults in parentheses.

| Setting | Applies to | Meaning |
|---|---|---|
| `VUS` (20; soak/chaos 10) | load, soak, chaos | Concurrent shoppers |
| `HOLD`, `RAMP` (3m, 1m) | load | Steady period; ramp up/down |
| `STEPS` (5,10,20,40,80,160), `STEP_S` (60), `MAX_VUS` (300) | stress | Iterations/second per step; seconds per step; VU ceiling |
| `BASE_VUS` (5), `SPIKE_FACTOR` (10), `HOLD_S` (120) | spike | Calm level, multiple, seconds at the peak |
| `SOAK_MINUTES` (30) | soak | Length (also `--minutes`) |
| `BASELINE_S` (45), `FAULT_S` (60), `RECOVERY_S` (120), `SETTLE_S` (30) | chaos | Seconds per phase; unjudged period after the fault |
| `USERS` (10) | all | Users logged in once and shared between VUs |
| `MIX_BROWSE` (0.7), `MIX_BASKET` (0.2) | all | Traffic mix; the remainder is checkout |
| `P95_BROWSE_MS`, `P95_DETAIL_MS`, `P95_ORDER_CREATE_MS`, `P95_CHECKOUT_MS`, `MAX_FAILED_RATE`, `MIN_CONFIRMED_RATE` | all | Pass/fail thresholds |
| `LOADTEST_STOCK` (1000000) | all | Stock for the dedicated product |
| `SETTLE_WAIT_S` (150) | run.sh | How long invariants may take to settle |

## Why it is built this way

- **Login cannot be load-tested, and the tests avoid it.** `POST /users/login` is limited to 10 per minute per IP on purpose (it is expensive by design and a brute-force target). So the tests log a small pool of users in once and share their tokens, retry on `429`, and the soak test re-logs-in each VU shortly before its one-hour token expires, staggered so they do not all hit the limit together.
- **Checkout needs its own stock.** It consumes real stock, so a run would otherwise exhaust a catalog product and turn into a test of the out-of-stock path. Given admin credentials, each run creates a product with a million units.
- **Stress uses an arrival rate, not a VU count.** VU-based tests slow down together with the system, which hides the breaking point.
- **k6 cannot break containers, so `run.sh` does.** The k6 script only generates traffic and labels each request with the phase it fell in (`baseline`, `fault`, `settling`, `recovery`); `run.sh` injects and restores the fault on a timeline measured from the moment traffic actually starts.
- **Per-phase numbers need a trick.** k6 records a tagged sub-metric only when a threshold mentions it, so `k6/lib/phases.js` adds always-passing thresholds for each phase. They are excluded from the report's pass/fail list.

## Cost of a run

Runs create real rows in the local stack: throwaway users, a `loadtest-...` product, and every order they placed. `scripts/cleanup.sh` removes exactly those, matched by name (`loadtest+%@example.com`, `loadtest-%`), and nothing else. The k6 results JSON deliberately omits the setup data, which holds those users' passwords and tokens.

## Testing a deployed environment

`run.sh` refuses anything but the local stack, so pointing a test at a real environment is always deliberate. To do it, run k6 directly against a target you own and are ready to overload:

```bash
docker run --rm -v "$PWD/loadtests/k6:/scripts:ro" -v "$PWD/loadtests/results:/results" \
  -e BASE_URL=https://your-apim-or-gateway -e ALLOW_REMOTE=true \
  -e ADMIN_EMAIL -e ADMIN_PASSWORD grafana/k6:2.3.0 run /scripts/load.js
```

That runs the traffic and thresholds only. The invariant checks and the fault injection read local containers, so they do not apply to a remote target; use the platform's own tooling (Azure Chaos Studio, the portal's dependency controls) for faults there, and query the databases directly for the invariants.

## Not covered yet

- **Network faults** (latency, packet loss) - only stop/pause/restart. Toxiproxy between the services would add them.
- **Live dashboards.** k6 can write to the Prometheus already in the stack (`-o experimental-prometheus-rw`, plus `--web.enable-remote-write-receiver` on Prometheus) so load and service metrics share a Grafana board. Not wired up.
- **CI.** These need the full stack and minutes to hours; they are run on demand, not per push.
- **Login endpoint performance**, by design (see above).
