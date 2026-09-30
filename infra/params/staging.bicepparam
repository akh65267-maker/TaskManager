using '../main.bicep'

// staging: production-shaped networking and edge, smaller and cheaper data tier.
// Private VNet, private endpoints, API Management in front of the gateway.

param environmentName = 'staging'
param networkMode = 'private'

param imageTag = readEnvironmentVariable('TM_IMAGE_TAG', 'latest')
param deployStage = readEnvironmentVariable('TM_DEPLOY_STAGE', 'foundation')

// ── data ─────────────────────────────────────────────────────────────────────
param postgresTopology = 'shared'
param postgresSkuName = 'Standard_B2s'
param postgresSkuTier = 'Burstable'
param postgresStorageGB = 64
param postgresHighAvailability = 'Disabled'
param postgresBackupRetentionDays = 14
param redisSkuName = 'Balanced_B0'
param redisPersistence = true

// ── platform ─────────────────────────────────────────────────────────────────
param acrSku = 'Standard'
param keyVaultPurgeProtection = true
param logRetentionDays = 60

param scale = {
  default: {
    cpu: '0.5'
    memory: '1Gi'
    minReplicas: 1
    maxReplicas: 3
  }
}

// ── app config ───────────────────────────────────────────────────────────────
// Replace with the staging frontend's real origin before deploying.
param corsAllowedOrigins = [
  'https://staging.example.com'
]
param adminEmail = readEnvironmentVariable('TM_ADMIN_EMAIL')

// ── edge / observability ─────────────────────────────────────────────────────
// StandardV2 (not Consumption): only v2/Developer/Premium tiers can reach a VNet.
param deployApim = true
param apimSku = 'StandardV2'
param apimRateLimitPerMinute = 600
param deployObservabilityStack = true
param exposeGrafana = false

// ── secrets (environment variables only) ─────────────────────────────────────
param postgresAdminPassword = readEnvironmentVariable('TM_POSTGRES_ADMIN_PASSWORD')
param jwtKey = readEnvironmentVariable('TM_JWT_KEY')
param rabbitmqPassword = readEnvironmentVariable('TM_RABBITMQ_PASSWORD')
param adminSeedPassword = readEnvironmentVariable('TM_ADMIN_SEED_PASSWORD')
param grafanaAdminPassword = readEnvironmentVariable('TM_GRAFANA_ADMIN_PASSWORD')
