using '../main.bicep'

// prod: private networking, zone-redundant Postgres, replicated apps.
//
// Read docs/azure-deployment.md "Production readiness" before using this. The
// template makes prod deployable; it does not make a single-node RabbitMQ on
// Azure Files production-grade - see the messaging notes there.

param environmentName = 'prod'
param networkMode = 'private'

param imageTag = readEnvironmentVariable('TM_IMAGE_TAG', 'latest')
param deployStage = readEnvironmentVariable('TM_DEPLOY_STAGE', 'foundation')

// ── data ─────────────────────────────────────────────────────────────────────
// One server per service, like compose: a noisy or failed database cannot take
// its neighbours with it. Switch to 'shared' to cut cost.
param postgresTopology = 'perService'
param postgresSkuName = 'Standard_D2ds_v5'
param postgresSkuTier = 'GeneralPurpose'
param postgresStorageGB = 128
param postgresHighAvailability = 'ZoneRedundant'
param postgresBackupRetentionDays = 35
param postgresGeoRedundantBackup = true
param redisSkuName = 'Balanced_B1'
param redisPersistence = true

// ── platform ─────────────────────────────────────────────────────────────────
param acrSku = 'Standard'
param keyVaultPurgeProtection = true
param logRetentionDays = 90

param scale = {
  default: {
    cpu: '0.5'
    memory: '1Gi'
    minReplicas: 2
    maxReplicas: 10
  }
  gateway: {
    minReplicas: 2
    maxReplicas: 10
  }
}

// ── app config ───────────────────────────────────────────────────────────────
// Replace with the production frontend's real origin before deploying.
param corsAllowedOrigins = [
  'https://www.example.com'
]
param adminEmail = readEnvironmentVariable('TM_ADMIN_EMAIL')

// ── edge / observability ─────────────────────────────────────────────────────
param deployApim = true
param apimSku = 'StandardV2'
param apimRateLimitPerMinute = 1200
param deployObservabilityStack = true
param exposeGrafana = false

// ── secrets (environment variables only) ─────────────────────────────────────
param postgresAdminPassword = readEnvironmentVariable('TM_POSTGRES_ADMIN_PASSWORD')
param jwtKey = readEnvironmentVariable('TM_JWT_KEY')
param rabbitmqPassword = readEnvironmentVariable('TM_RABBITMQ_PASSWORD')
param adminSeedPassword = readEnvironmentVariable('TM_ADMIN_SEED_PASSWORD')
param grafanaAdminPassword = readEnvironmentVariable('TM_GRAFANA_ADMIN_PASSWORD')
