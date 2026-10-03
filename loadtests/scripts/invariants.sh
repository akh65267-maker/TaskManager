#!/usr/bin/env bash
# What must be true AFTER a load or chaos run, whatever happened during it.
#
# Latency and error rate tell you how the system felt under pressure. They say
# nothing about whether it stayed correct, and correctness is what overload and
# failure usually damage: an order stuck forever, stock that vanished or appeared, a
# message that never left the outbox, a poisoned queue. These read the databases and
# the broker directly, so they do not depend on the API that was just being abused.
#
#   invariants.sh --start <UTC timestamp> [--products <id:stock,id:stock,...>]
#                 [--errors-before <n>] [--wait <s>]
#
# --products   each product the run used and the stock it started with. Stock is
#              checked per product, not in total: one product losing a unit while
#              another gains one would cancel out in a sum and hide both bugs.
# --errors-before  how many messages already sat in _error queues when the run began,
#              so leftovers from an earlier run do not fail this one.
#
# --wait keeps re-checking for up to that many seconds, because the system is
# eventually consistent: the saga timeout, late stock releases and outbox delivery
# are still catching up when the load stops. A failure that persists after the wait
# is real; one that clears was just in flight.

set -uo pipefail

START="" PRODUCTS="" ERRORS_BEFORE=0 WAIT=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --start)          START="$2"; shift 2 ;;
    --products)       PRODUCTS="$2"; shift 2 ;;
    --errors-before)  ERRORS_BEFORE="$2"; shift 2 ;;
    --wait)           WAIT="$2"; shift 2 ;;
    *) echo "unknown option $1" >&2; exit 3 ;;
  esac
done
[[ -n "$START" ]] || { echo "--start is required" >&2; exit 3; }

# Only ever spliced into SQL from values this script or run.sh produced, but validate
# anyway: it costs nothing and these run against a database.
[[ "$START" =~ ^[0-9T:.Z+-]+$ ]] || { echo "bad --start" >&2; exit 3; }
[[ "$ERRORS_BEFORE" =~ ^[0-9]+$ ]] || { echo "bad --errors-before" >&2; exit 3; }
[[ -z "$PRODUCTS" || "$PRODUCTS" =~ ^([0-9a-fA-F-]{36}:[0-9]+)(,[0-9a-fA-F-]{36}:[0-9]+)*$ ]] \
  || { echo "bad --products (expected id:stock,id:stock)" >&2; exit 3; }

order_db()     { docker exec taskflow-postgres-order psql -U postgres -d orderflow -tAqc "$1" 2>/dev/null | tr -d '[:space:]'; }
inventory_db() { docker exec taskflow-postgres-inventory psql -U postgres -d inventoryflow -tAqc "$1" 2>/dev/null | tr -d '[:space:]'; }

FAILED=0
UNKNOWN=0
LINES=()

# status: 0 = holds, 1 = VIOLATED, 2 = could not be checked. The distinction matters: a
# database that is out of connections after an overload cannot be queried, and "I could
# not look" is not the same finding as "I looked and it is wrong".
record() { # name, status(0/1/2), detail
  local mark="PASS"
  if [[ "$2" -eq 1 ]]; then mark="FAIL"; FAILED=1; fi
  if [[ "$2" -eq 2 ]]; then mark="????"; UNKNOWN=1; fi
  LINES+=("$(printf '  %-4s  %-34s %s' "$mark" "$1" "$3")")
}

evaluate() {
  FAILED=0
  UNKNOWN=0
  LINES=()

  local total confirmed cancelled pending
  total=$(order_db "select count(*) from \"Orders\" where \"CreatedAtUtc\" >= '$START'")
  confirmed=$(order_db "select count(*) from \"Orders\" where \"Status\"='Confirmed' and \"CreatedAtUtc\" >= '$START'")
  cancelled=$(order_db "select count(*) from \"Orders\" where \"Status\"='Cancelled' and \"CreatedAtUtc\" >= '$START'")
  pending=$(order_db "select count(*) from \"Orders\" where \"Status\"='Pending' and \"CreatedAtUtc\" >= '$START'")

  # 1. Nothing is left waiting. Every order must have been confirmed or cancelled,
  #    by the saga or by its timeout.
  if [[ -z "$pending" ]]; then record "no order left Pending" 2 "could not query the order database (unreachable, or out of connections)"
  else record "no order left Pending" "$([[ "$pending" -eq 0 ]] && echo 0 || echo 1)" "$pending pending of $total created ($confirmed confirmed, $cancelled cancelled)"; fi

  # 2. No saga is still waiting for stock responses.
  local waiting
  waiting=$(order_db "select count(*) from \"OrderSagaStates\" where \"CurrentState\"='AwaitingStockReservation'")
  if [[ -z "$waiting" ]]; then record "no saga still awaiting stock" 2 "could not query the order database"
  else record "no saga still awaiting stock" "$([[ "$waiting" -eq 0 ]] && echo 0 || echo 1)" "$waiting awaiting"; fi

  # 3. Stock is conserved: every unit that left stock belongs to a confirmed order.
  #    Cancelled orders must have given theirs back. A positive difference is stock
  #    that vanished (a reservation nothing released); a negative one is stock that
  #    appeared (something released twice).
  if [[ -n "$PRODUCTS" ]]; then
    local entry product initial sold final expected diff n=0 bad=0 unreadable=0 problems=""
    local total_sold=0
    for entry in ${PRODUCTS//,/ }; do
      product="${entry%%:*}"; initial="${entry##*:}"; n=$((n + 1))
      sold=$(order_db "select coalesce(sum(i.\"Quantity\"),0) from \"OrderItem\" i join \"Orders\" o on o.\"Id\"=i.\"OrderId\" where o.\"Status\"='Confirmed' and o.\"CreatedAtUtc\" >= '$START' and i.\"ProductId\"='$product'")
      final=$(inventory_db "select \"QuantityAvailable\" from \"InventoryItems\" where \"ProductId\"='$product'")
      if [[ -z "$sold" || -z "$final" ]]; then
        unreadable=$((unreadable + 1)); problems+="${product:0:8}: unreadable; "
        continue
      fi
      total_sold=$((total_sold + sold))
      expected=$((initial - sold))
      diff=$((expected - final))
      if [[ "$diff" -ne 0 ]]; then
        bad=$((bad + 1))
        problems+="${product:0:8}: expected $expected found $final ($([[ $diff -gt 0 ]] && echo "$diff LOST" || echo "$((-diff)) CREATED")); "
      fi
    done
    # A product that could not be read is unknown, not wrong; a wrong one wins over it.
    local stock_status=0
    [[ "$unreadable" -gt 0 ]] && stock_status=2
    [[ "$bad" -gt 0 ]] && stock_status=1
    record "stock conserved (per product)" "$stock_status" \
      "$n product(s), $total_sold units sold${problems:+, PROBLEMS: $problems}"
  fi

  # 4. The outbox drained: nothing committed is still waiting to reach the broker.
  local order_outbox inventory_outbox
  order_outbox=$(order_db "select count(*) from \"OutboxMessage\"")
  inventory_outbox=$(inventory_db "select count(*) from \"OutboxMessage\"")
  if [[ -z "$order_outbox" || -z "$inventory_outbox" ]]; then
    record "outbox drained" 2 "order ${order_outbox:-?}, inventory ${inventory_outbox:-?} (a database could not be queried)"
  else
    record "outbox drained" "$([[ "$order_outbox" -eq 0 && "$inventory_outbox" -eq 0 ]] && echo 0 || echo 1)" \
      "order $order_outbox, inventory $inventory_outbox undelivered"
  fi

  # 5. No message exhausted its retries during THIS run and was parked in an _error
  #    queue. Nothing consumes those, so a message there is a failure that would
  #    otherwise be silent. Compared with what was already there at the start, so
  #    leftovers from an earlier run do not fail this one.
  local listing errors_now new
  listing=$(docker exec taskflow-rabbitmq rabbitmqctl list_queues name messages --no-table-headers 2>/dev/null \
    | awk '$1 ~ /_error$/ && $2 > 0 { printf "%s(%s) ", $1, $2 }')
  errors_now=$(docker exec taskflow-rabbitmq rabbitmqctl list_queues name messages --no-table-headers 2>/dev/null \
    | awk '$1 ~ /_error$/ { s += $2 } END { print s + 0 }')
  new=$((errors_now - ERRORS_BEFORE))
  record "no new messages in _error queues" "$([[ "$new" -le 0 ]] && echo 0 || echo 1)" \
    "$([[ "$new" -gt 0 ]] && echo "$new NEW (now: $listing)" || echo "none new${listing:+ ($ERRORS_BEFORE already there: $listing)}")"
}

deadline=$(( $(date +%s) + WAIT ))
while :; do
  evaluate
  [[ "$FAILED" -eq 0 && "$UNKNOWN" -eq 0 ]] && break
  [[ "$(date +%s)" -ge "$deadline" ]] && break
  sleep 5
done

echo
echo "Invariants after the run:"
printf '%s\n' "${LINES[@]}"
echo
if [[ "$FAILED" -ne 0 ]]; then echo "  INVARIANTS VIOLATED."; exit 1; fi
if [[ "$UNKNOWN" -ne 0 ]]; then echo "  INCONCLUSIVE: some invariants could not be checked (see ????). Nothing was found wrong, but nothing was proven either; re-run invariants.sh once the system is quiet."; exit 4; fi
echo "  All invariants hold."
exit 0
