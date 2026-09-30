#!/usr/bin/env bash
# Faults that run.sh can inject during a resilience test. Sourced, not executed.
#
# Two failure shapes matter and behave differently:
#   stop    the dependency is GONE: connections are refused immediately.
#   pause   the dependency HANGS: connections stay open and nothing answers, which is
#           what a saturated or partitioned service looks like, and is the case that
#           exposes missing timeouts (a refused connection fails fast; a hang does not).

declare -A FAULT_CONTAINER=(
  [inventory-down]=taskflow-inventory-api
  [inventory-hang]=taskflow-inventory-api
  [catalog-down]=taskflow-catalog-api
  [basket-down]=taskflow-basket-api
  [rabbitmq-down]=taskflow-rabbitmq
  [rabbitmq-hang]=taskflow-rabbitmq
  [order-db-down]=taskflow-postgres-order
  [order-db-hang]=taskflow-postgres-order
  [inventory-db-down]=taskflow-postgres-inventory
  [redis-down]=taskflow-redis
  [order-restart]=taskflow-order-api
  [gateway-down]=taskflow-gateway
)

declare -A FAULT_ACTION=(
  [inventory-down]=stop
  [inventory-hang]=pause
  [catalog-down]=stop
  [basket-down]=stop
  [rabbitmq-down]=stop
  [rabbitmq-hang]=pause
  [order-db-down]=stop
  [order-db-hang]=pause
  [inventory-db-down]=stop
  [redis-down]=stop
  [order-restart]=restart
  [gateway-down]=stop
)

fault_names() { printf '%s\n' "${!FAULT_CONTAINER[@]}" | sort; }

fault_known() { [[ -n "${FAULT_CONTAINER[$1]:-}" ]]; }

fault_inject() {
  local name="$1" container="${FAULT_CONTAINER[$1]}" action="${FAULT_ACTION[$1]}"
  case "$action" in
    stop)    docker stop "$container" >/dev/null ;;
    pause)   docker pause "$container" >/dev/null ;;
    restart) docker restart "$container" >/dev/null ;;
  esac
}

fault_restore() {
  local name="$1" container="${FAULT_CONTAINER[$1]}" action="${FAULT_ACTION[$1]}"
  case "$action" in
    stop)    docker start "$container" >/dev/null ;;
    pause)   docker unpause "$container" >/dev/null ;;
    restart) : ;; # nothing to undo; the restart was the fault
  esac
}

# Safety net for an interrupted run: leaves nothing stopped or frozen. Harmless on
# containers that are already fine.
fault_restore_all() {
  local container
  for container in $(printf '%s\n' "${FAULT_CONTAINER[@]}" | sort -u); do
    docker unpause "$container" >/dev/null 2>&1 || true
    docker start "$container" >/dev/null 2>&1 || true
  done
}
