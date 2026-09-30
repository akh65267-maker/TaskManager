using '../main.bicep'

// dev: cheapest viable shape. Public endpoints behind authentication, one shared
// Postgres server, apps scale to zero, no API Management.
//
// Secrets are NOT in this file. They are read from environment variables at
// deploy time (infra/scripts/deploy-azure.sh generates and exports them).

param environmentName = 'dev'
param networkMode = 'public'

param imageTag = readEnvironmentVariable('TM_IMAGE_TAG', 'latest')
param deployStage = readEnvironmentVariable('TM_DEPLOY_STAGE', 'foundation')

// ── data ─────────────────────────────────────────────────────────────────────
param postgresTopology = 'shared'
param postgresSkuName = 'Standard_B1ms'
param postgresSkuTier = 'Burstable'
param postgresStorageGB = 32
param postgresHighAvailability = 'Disabled'
param redisSkuName = 'Balanced_B0'
param redisPersistence = false

// ── platform ─────────────────────────────────────────────────────────────────
param acrSku = 'Basic'
param keyVaultPurgeProtection = false
param logRetentionDays = 30

// ── app sizing: scale to zero when idle, except where a background job lives ──
param scale = {
  default: {
    cpu: '0.5'
    memory: '1Gi'
    minReplicas: 0
    maxReplicas: 2
  }
  // OrderService hosts the checkout timeout sweeper and the outbox delivery
  // poller. Scaled to zero, neither runs and stranded checkouts are never cancelled.
  'order-api': {
    minReplicas: 1
  }
}

// ── app config ───────────────────────────────────────────────────────────────
// The local frontend (npm run dev on :3100) can call the Azure dev backend.
param corsAllowedOrigins = [
  'http://localhost:3100'
]
param adminEmail = readEnvironmentVariable('TM_ADMIN_EMAIL', 'admin@example.com')

// ── edge / observability ─────────────────────────────────────────────────────
param deployApim = false
param deployObservabilityStack = true
param exposeGrafana = true

// ── secrets (environment variables only) ─────────────────────────────────────
param postgresAdminPassword = readEnvironmentVariable('TM_POSTGRES_ADMIN_PASSWORD')
param jwtKey = readEnvironmentVariable('TM_JWT_KEY')
param rabbitmqPassword = readEnvironmentVariable('TM_RABBITMQ_PASSWORD')
param adminSeedPassword = readEnvironmentVariable('TM_ADMIN_SEED_PASSWORD')
param grafanaAdminPassword = readEnvironmentVariable('TM_GRAFANA_ADMIN_PASSWORD')
