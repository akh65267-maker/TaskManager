#!/usr/bin/env bash
# Removes what the load tests created in the LOCAL docker-compose stack.
#
#   cleanup.sh [--yes]
#
# Runs create real rows: throwaway users, a "loadtest-..." product with a million units
# of stock, and every order those users placed. This deletes exactly those and nothing
# else, matched by name so it cannot touch real data:
#   users     email like 'loadtest+%@example.com'
#   products  name  like 'loadtest-%'
#   plus their orders, order items, sagas, stock records and baskets.
#
# It works on the containers directly (docker exec), so it needs no credentials.

set -uo pipefail

ASSUME_YES=false
[[ "${1:-}" == "--yes" ]] && ASSUME_YES=true

users_db()     { docker exec taskflow-postgres-users     psql -U postgres -d userflow      -tAqc "$1"; }
catalog_db()   { docker exec taskflow-postgres-catalog   psql -U postgres -d catalogflow   -tAqc "$1"; }
inventory_db() { docker exec taskflow-postgres-inventory psql -U postgres -d inventoryflow -tAqc "$1"; }
order_db()     { docker exec taskflow-postgres-order     psql -U postgres -d orderflow     -tAqc "$1"; }

# Ids come back from the databases, so check they are UUIDs before splicing them into
# another statement.
uuids() { grep -E '^[0-9a-fA-F-]{36}$' | sed "s/.*/'&'/" | paste -sd, -; }

USER_IDS="$(users_db "select \"Id\" from \"Users\" where \"Email\" like 'loadtest+%@example.com'" | uuids)"
PRODUCT_IDS="$(catalog_db "select \"Id\" from \"Products\" where \"Name\" like 'loadtest-%'" | uuids)"

if [[ -z "$USER_IDS" && -z "$PRODUCT_IDS" ]]; then
  echo "Nothing to clean up."
  exit 0
fi

orders=0
[[ -n "$USER_IDS" ]] && orders="$(order_db "select count(*) from \"Orders\" where \"UserId\" in ($USER_IDS)" | tr -d '[:space:]')"
echo "Found: $(grep -o "'" <<<"$USER_IDS" | wc -l | awk '{print $1/2}') test users, $(grep -o "'" <<<"$PRODUCT_IDS" | wc -l | awk '{print $1/2}') test products, ${orders:-0} orders."

if ! $ASSUME_YES; then
  read -r -p "Delete them from the local stack? [y/N] " answer
  [[ "$answer" =~ ^[Yy]$ ]] || { echo "aborted"; exit 1; }
fi

if [[ -n "$USER_IDS" ]]; then
  order_db "delete from \"OrderItem\" where \"OrderId\" in (select \"Id\" from \"Orders\" where \"UserId\" in ($USER_IDS))" >/dev/null
  order_db "delete from \"OrderSagaStates\" where \"CorrelationId\" in (select \"Id\" from \"Orders\" where \"UserId\" in ($USER_IDS))" >/dev/null
  order_db "delete from \"Orders\" where \"UserId\" in ($USER_IDS)" >/dev/null

  for id in $(tr -d "'" <<<"$USER_IDS" | tr ',' ' '); do
    docker exec taskflow-redis redis-cli del "basket:$id" >/dev/null
  done
  users_db "delete from \"Users\" where \"Id\" in ($USER_IDS)" >/dev/null
fi

if [[ -n "$PRODUCT_IDS" ]]; then
  inventory_db "delete from \"InventoryItems\" where \"ProductId\" in ($PRODUCT_IDS)" >/dev/null
  catalog_db "delete from \"Products\" where \"Id\" in ($PRODUCT_IDS)" >/dev/null
fi

echo "Done."
