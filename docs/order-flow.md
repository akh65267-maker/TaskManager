# Order flow (checkout saga)

Orchestrated by `OrderSagaStateMachine` in OrderService. One saga instance per order, correlated by `OrderId` on all three events. One `ReserveStock` command per line item; the saga finalizes once it has received one response per item.

## Flow

```text
Client
  │ POST /orders  (Bearer JWT; items incl. client-supplied UnitPrice)
  ▼
ApiGateway  →  OrderService.OrdersService.CreateAsync
  │
  ├── new Order(userId, items)          → Status = Pending
  ├── Publish(OrderSubmitted)           → buffered in outbox
  └── SaveChangesAsync                  → order row + outbox msg, ONE transaction
  │
  │ 201 Created { id }    ← returned immediately; checkout is still in progress
  ▼
OrderSubmitted  ──▶  Saga (Initially)
        stores UserId, TotalItems, ItemsJson, ResponseCount=0, HasFailure=false
        Publish ReserveStock ×N  →  TransitionTo(AwaitingStockReservation)
  ▼
InventoryService.ReserveStockConsumer      (inbox dedup + outbox, per message)
  ├── no inventory record → StockReservationFailed("No inventory record …")
  ├── item.Reserve(qty) OK → StockReserved            (stock decremented)
  └── InsufficientStockException → StockReservationFailed(ex.Message)
  ▼
Saga (During AwaitingStockReservation)
  ├── StockReserved          → ResponseCount++, append ProductId to ReservedProductIds
  └── StockReservationFailed → HasFailure=true, FailureReason ??= Reason, ResponseCount++
  │
  └── when ResponseCount >= TotalItems  →  FinalizeAsync + Finalize()
        ├── HasFailure = false → order.Confirm()                    → Confirmed
        └── HasFailure = true  → Publish ReleaseStock for every
                                 already-reserved ProductId,
                                 order.Cancel(FailureReason)        → Cancelled
        └── repo.SaveChangesAsync()
  ▼
ReleaseStockConsumer  →  item.Release(qty)   (compensation, stock restored)
```

`SetCompletedWhenFinalized()` means the saga row is deleted once finalized — there is no post-mortem record of a completed checkout beyond the order row and the logs.

## State

`OrderSagaState` (`OrderSagaStates` table, PK `CorrelationId` = `OrderId`, `RowVersion` as EF row-version):

| Field | Purpose |
|---|---|
| `CurrentState` | `Initial` → `AwaitingStockReservation` → finalized (row removed) |
| `TotalItems` / `ResponseCount` | completion counter; finalize when `ResponseCount >= TotalItems` |
| `HasFailure` / `FailureReason` | first failure reason wins (`??=`), becomes the order's `CancellationReason` |
| `ItemsJson` | serialized `OrderLineItem[]`, needed to know quantities when compensating |
| `ReservedProductIdsJson` | which products actually got reserved, so only those are released |

`ItemsJson`/`ReservedProductIdsJson` are JSON strings rather than owned EF collections, deliberately, to keep the saga mapping simple.

## Failure and consistency characteristics

- **Compensation is verified by test**, not just by inspection: `Checkout_WithInsufficientStock_CancelsOrderAndReleasesReservedItems` asserts the plentiful product's stock returns to its original value while the scarce one is untouched.
- **Partial reservations are visible to other callers** while the saga is in flight. Reserving decrements `QuantityAvailable` immediately, so between reservation and compensation `GET /inventory` reports the lower number. This is compensating-transaction (eventual) consistency, not isolation.
- **`FinalizeAsync` writes the order in a different transaction from the saga state.** If `repo.SaveChangesAsync()` fails after the saga row was persisted/removed, the order can stay `Pending` while the saga is gone. `UseMessageRetry` mitigates but does not eliminate this.
- **`Confirm()`/`Cancel()` throw unless the order is `Pending`**, so a second finalize attempt on an already-resolved order faults the message rather than being a no-op.
- **No timeout.** No MassTransit scheduler is configured and the saga defines no `Schedule`. If a `ReserveStock` is never consumed or a response is permanently lost (e.g. it exhausted retries into the `_error` queue), the saga waits in `AwaitingStockReservation` forever and the order stays `Pending` indefinitely, with any partial reservations never released.
- **The client is not told the outcome.** `POST /orders` returns `201` immediately; the only way to learn the result is polling `GET /orders/{id}` (which is exactly what the integration test does). There is no callback, webhook, SSE or notification event.
- Duplicate stock responses are a real risk because the saga endpoint has no inbox — see [TODO.md](TODO.md).
