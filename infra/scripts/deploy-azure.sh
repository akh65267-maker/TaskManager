#!/usr/bin/env bash
# Deploys one TaskManager environment to Azure.
#
# Nothing in this repository runs this automatically. It needs the Azure CLI, an
# active login (`az login`) and a subscription you are prepared to spend money in.
#
# Usage:
#   deploy-azure.sh <dev|staging|prod> [stage] [options]
#
# Stages (default: all). `all` runs the first four, in order, every time:
#   foundation  registry, Key Vault, databases, Redis, Container Apps environment
#   images      build every image in ACR (needs foundation)
#   migrate     add the migration job, run it, wait for it to succeed
#   apps        add every application, Prometheus/Grafana and optional APIM
#
# Options:
#   --location <region>        Azure region (required the first time; or TM_LOCATION)
#   --resource-group <name>    default: rg-taskmanager-<env>
#   --tag <tag>                image tag, default: current git short SHA
#   --generate-secrets         create any missing secrets in infra/.secrets/<env>.env
#   --what-if                  preview the infrastructure change instead of applying it
#   --yes                      skip the "deploy to this subscription?" prompt
#
# Secrets are read from TM_* environment variables or infra/.secrets/<env>.env
# (git-ignored). They are handed to Bicep as secure parameters and stored in Key
# Vault; they are never written to a file by this script except by
# --generate-secrets, and never passed on a command line.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
INFRA_DIR="$(dirname "$SCRIPT_DIR")"
REPO_ROOT="$(dirname "$INFRA_DIR")"

die() { echo "error: $*" >&2; exit 1; }
info() { echo "==> $*"; }

usage() { sed -n '2,/^set -euo/p' "${BASH_SOURCE[0]}" | sed '$d' | sed 's/^# \{0,1\}//'; }

# ── arguments ────────────────────────────────────────────────────────────────

[[ $# -ge 1 ]] || { usage; exit 1; }
[[ "$1" == "-h" || "$1" == "--help" ]] && { usage; exit 0; }

ENVIRONMENT="$1"; shift
case "$ENVIRONMENT" in dev|staging|prod) ;; *) die "environment must be dev, staging or prod (got '$ENVIRONMENT')";; esac

STAGE="all"
if [[ $# -ge 1 && "$1" != --* ]]; then STAGE="$1"; shift; fi
case "$STAGE" in all|foundation|images|migrate|apps) ;; *) die "unknown stage '$STAGE'";; esac

LOCATION="${TM_LOCATION:-}"
RESOURCE_GROUP="rg-taskmanager-${ENVIRONMENT}"
TAG=""
GENERATE_SECRETS=false
WHAT_IF=false
ASSUME_YES=false

while [[ $# -gt 0 ]]; do
  case "$1" in
    --location) LOCATION="${2:?--location needs a value}"; shift 2 ;;
    --resource-group) RESOURCE_GROUP="${2:?--resource-group needs a value}"; shift 2 ;;
    --tag) TAG="${2:?--tag needs a value}"; shift 2 ;;
    --generate-secrets) GENERATE_SECRETS=true; shift ;;
    --what-if) WHAT_IF=true; shift ;;
    --yes) ASSUME_YES=true; shift ;;
    *) die "unknown option '$1'" ;;
  esac
done

if [[ -z "$TAG" ]]; then
  TAG="$(git -C "$REPO_ROOT" rev-parse --short HEAD 2>/dev/null || true)"
  [[ -n "$TAG" ]] || die "no --tag given and this is not a git checkout"
  if [[ -n "$(git -C "$REPO_ROOT" status --porcelain 2>/dev/null)" ]]; then
    echo "warning: working tree has uncommitted changes; image tag '$TAG' will not identify what was built" >&2
  fi
fi

# ── secrets ──────────────────────────────────────────────────────────────────

SECRETS_DIR="$INFRA_DIR/.secrets"
SECRETS_FILE="$SECRETS_DIR/${ENVIRONMENT}.env"
REQUIRED_SECRETS=(TM_POSTGRES_ADMIN_PASSWORD TM_JWT_KEY TM_RABBITMQ_PASSWORD TM_ADMIN_SEED_PASSWORD TM_GRAFANA_ADMIN_PASSWORD)

# Hex only: these values are embedded in Npgsql connection strings, where ';' and
# '=' would need escaping.
random_hex() { openssl rand -hex "$1"; }

generate_missing_secrets() {
  command -v openssl >/dev/null || die "openssl is required for --generate-secrets"
  umask 077
  mkdir -p "$SECRETS_DIR"
  touch "$SECRETS_FILE"
  # Explicit as well as umask: NTFS-backed mounts (a repo under /mnt/c) ignore
  # umask. There this is a no-op and Windows ACLs decide who can read the file,
  # so keep the checkout on a Linux filesystem if others share the machine.
  chmod 600 "$SECRETS_FILE"
  local name bytes
  for name in "${REQUIRED_SECRETS[@]}"; do
    grep -q "^${name}=" "$SECRETS_FILE" && continue
    [[ "$name" == "TM_JWT_KEY" ]] && bytes=48 || bytes=24
    echo "${name}=$(random_hex "$bytes")" >> "$SECRETS_FILE"
    info "generated $name in $SECRETS_FILE"
  done
}

$GENERATE_SECRETS && generate_missing_secrets

if [[ -f "$SECRETS_FILE" ]]; then
  set -a; source "$SECRETS_FILE"; set +a
fi

missing=()
for name in "${REQUIRED_SECRETS[@]}"; do [[ -n "${!name:-}" ]] || missing+=("$name"); done
if [[ "$ENVIRONMENT" != "dev" && -z "${TM_ADMIN_EMAIL:-}" ]]; then missing+=("TM_ADMIN_EMAIL"); fi
if [[ ${#missing[@]} -gt 0 ]]; then
  die "missing: ${missing[*]}
  Export them, put them in $SECRETS_FILE, or re-run with --generate-secrets.
  Re-running with a different value rotates the secret for every service on the next deploy."
fi

# ── prerequisites ────────────────────────────────────────────────────────────

command -v az >/dev/null || die "the Azure CLI (az) is not installed"
az account show >/dev/null 2>&1 || die "not logged in: run 'az login' (or use azure/login in CI)"

SUBSCRIPTION_NAME="$(az account show --query name -o tsv)"
SUBSCRIPTION_ID="$(az account show --query id -o tsv)"

info "environment:    $ENVIRONMENT"
info "stage:          $STAGE"
info "subscription:   $SUBSCRIPTION_NAME ($SUBSCRIPTION_ID)"
info "resource group: $RESOURCE_GROUP"
info "image tag:      $TAG"

if ! $ASSUME_YES && ! $WHAT_IF; then
  read -r -p "This creates billable Azure resources. Continue? [y/N] " answer
  [[ "$answer" =~ ^[Yy]$ ]] || die "aborted"
fi

# ── steps ────────────────────────────────────────────────────────────────────

ensure_resource_group() {
  if ! az group show --name "$RESOURCE_GROUP" >/dev/null 2>&1; then
    [[ -n "$LOCATION" ]] || die "resource group '$RESOURCE_GROUP' does not exist; pass --location (or set TM_LOCATION)"
    info "creating resource group $RESOURCE_GROUP in $LOCATION"
    az group create --name "$RESOURCE_GROUP" --location "$LOCATION" --output none
  fi
}

# Runs the Bicep deployment for one stage. TM_DEPLOY_STAGE / TM_IMAGE_TAG are read
# by params/<env>.bicepparam through readEnvironmentVariable().
deploy_stage() {
  local stage="$1"
  local name="taskmanager-${ENVIRONMENT}-${stage}-$(date +%Y%m%d%H%M%S)"
  info "deploying stage '$stage'"
  if $WHAT_IF; then
    TM_DEPLOY_STAGE="$stage" TM_IMAGE_TAG="$TAG" \
      az deployment group what-if --resource-group "$RESOURCE_GROUP" --name "$name" \
        --parameters "$INFRA_DIR/params/${ENVIRONMENT}.bicepparam"
    return
  fi
  TM_DEPLOY_STAGE="$stage" TM_IMAGE_TAG="$TAG" \
    az deployment group create --resource-group "$RESOURCE_GROUP" --name "$name" \
      --parameters "$INFRA_DIR/params/${ENVIRONMENT}.bicepparam" --output none
  LAST_DEPLOYMENT="$name"
}

output_of() {
  az deployment group show --resource-group "$RESOURCE_GROUP" --name "$LAST_DEPLOYMENT" \
    --query "properties.outputs.$1.value" --output tsv
}

registry_name() {
  # Works even when this run skipped the foundation stage.
  az acr list --resource-group "$RESOURCE_GROUP" --query "[0].name" --output tsv
}

build_images() {
  local registry; registry="$(registry_name)"
  [[ -n "$registry" ]] || die "no container registry in $RESOURCE_GROUP - run the foundation stage first"

  # name|build context|dockerfile (relative to the context)
  # Application images use src/ as their context, exactly as docker-compose does.
  # The infra images use the repository root, which is why a root .dockerignore exists.
  local images=(
    "users-api|src|Services/UserService/Dockerfile"
    "catalog-api|src|Services/CatalogService/Dockerfile"
    "basket-api|src|Services/BasketService/Dockerfile"
    "inventory-api|src|Services/InventoryService/Dockerfile"
    "order-api|src|Services/OrderService/Dockerfile"
    "gateway|src|Gateway/ApiGateway/Dockerfile"
    "rabbitmq|.|infra/images/rabbitmq/Dockerfile"
    "prometheus|.|infra/images/prometheus/Dockerfile"
    "grafana|.|infra/images/grafana/Dockerfile"
    "migrations|.|infra/images/migrations/Dockerfile"
  )

  local entry name context file
  for entry in "${images[@]}"; do
    IFS='|' read -r name context file <<< "$entry"
    info "building $name:$TAG in $registry"
    az acr build --registry "$registry" --image "${name}:${TAG}" \
      --file "$file" "$REPO_ROOT/$context" --no-logs >/dev/null \
      || az acr build --registry "$registry" --image "${name}:${TAG}" --file "$file" "$REPO_ROOT/$context"
  done
}

run_migrations() {
  local job="migrations" execution status waited=0
  info "starting migration job"
  execution="$(az containerapp job start --name "$job" --resource-group "$RESOURCE_GROUP" --query name --output tsv)"
  while :; do
    status="$(az containerapp job execution show --name "$job" --resource-group "$RESOURCE_GROUP" \
      --job-execution-name "$execution" --query properties.status --output tsv 2>/dev/null || echo Unknown)"
    case "$status" in
      Succeeded) info "migrations succeeded"; return 0 ;;
      Failed|Degraded|Stopped)
        echo "migration execution '$execution' ended as $status. Logs:" >&2
        az containerapp job logs show --name "$job" --resource-group "$RESOURCE_GROUP" \
          --execution "$execution" --container migrate --format text 2>&1 | tail -40 >&2 || true
        die "migrations did not succeed; applications were NOT deployed" ;;
    esac
    (( waited >= 1200 )) && die "migrations still '$status' after 20 minutes"
    sleep 10; waited=$((waited + 10))
  done
}

summary() {
  info "done"
  local gateway apim grafana
  gateway="$(output_of gatewayFqdn || true)"
  apim="$(output_of apimGatewayUrl || true)"
  grafana="$(output_of grafanaFqdn || true)"
  [[ -n "$apim" ]]    && echo "  API (APIM):  $apim"
  [[ -n "$gateway" ]] && echo "  Gateway:     https://$gateway"
  [[ -n "$grafana" ]] && echo "  Grafana:     https://$grafana"
  return 0
}

LAST_DEPLOYMENT=""
ensure_resource_group

case "$STAGE" in
  foundation) deploy_stage foundation ;;
  images)     build_images ;;
  migrate)    deploy_stage migrate; $WHAT_IF || run_migrations ;;
  apps)       deploy_stage apps; $WHAT_IF || summary ;;
  all)
    deploy_stage foundation
    if $WHAT_IF; then
      info "--what-if previews the foundation only; later stages need images that do not exist yet"
    else
      build_images
      deploy_stage migrate
      run_migrations
      deploy_stage apps
      summary
    fi ;;
esac
