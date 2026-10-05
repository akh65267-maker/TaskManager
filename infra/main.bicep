// TaskManager on Azure Container Apps.
//
// Deploys ONE environment (dev, staging or prod) into an existing resource
// group; the differences between environments live entirely in params/*.bicepparam.
// Nothing here is deployed by the repository's own tooling - see
// docs/azure-deployment.md and infra/scripts/deploy-azure.sh.
//
// Staged by design (deployStage). Container apps cannot start until their images
// exist in ACR, and ACR is created by this same template; and services must not
// start against empty databases, because nothing applies migrations at startup:
//   1. foundation -> ACR, Key Vault, databases, Redis, environment, ...
//   2. build and push images
//   3. migrate    -> adds the migration job; the script runs it and waits
//   4. apps       -> adds every application, Prometheus/Grafana, optional APIM

targetScope = 'resourceGroup'

// ───────────────────────────── identity of the deployment ─────────────────────

@description('Short environment name; part of every resource name.')
param environmentName string

param location string = resourceGroup().location

@description('Prefix for resource names. Lowercase letters/digits only.')
@minLength(2)
@maxLength(6)
param namePrefix string = 'tm'

param tags object = {}

@description('Image tag for every application image, normally the git SHA.')
param imageTag string = 'latest'

@description('foundation = registry, vault, databases, environment. migrate = also the migration job. apps = also every application. Each stage needs the previous one done and (from migrate on) its images already in ACR.')
@allowed(['foundation', 'migrate', 'apps'])
param deployStage string = 'foundation'

// ───────────────────────────────── networking ─────────────────────────────────

@description('public = PaaS endpoints on the internet behind auth; private = VNet-injected, private endpoints, internal Container Apps environment.')
@allowed(['public', 'private'])
param networkMode string = 'public'

param vnetAddressPrefix string = '10.40.0.0/16'

@description('CIDRs allowed to reach the gateway when it is public. Empty = anywhere. Set to the APIM outbound IP to make APIM the only way in.')
param gatewayAllowedCidrs array = []

// ─────────────────────────────── data services ────────────────────────────────

@description('shared = one server, one database per service. perService = a server per service (physical isolation, like compose).')
@allowed(['shared', 'perService'])
param postgresTopology string = 'shared'

param postgresSkuName string = 'Standard_B1ms'

@allowed(['Burstable', 'GeneralPurpose', 'MemoryOptimized'])
param postgresSkuTier string = 'Burstable'

param postgresStorageGB int = 32

@allowed(['Disabled', 'ZoneRedundant', 'SameZone'])
param postgresHighAvailability string = 'Disabled'

param postgresBackupRetentionDays int = 7
param postgresGeoRedundantBackup bool = false
param postgresAdminLogin string = 'tmadmin'

@description('Npgsql options appended to every connection string. Require encrypts without verifying the server certificate; tighten to VerifyFull once the CA chain is confirmed.')
param postgresConnectionOptions string = 'SSL Mode=Require;Trust Server Certificate=true'

@description('Npgsql Maximum Pool Size for every service (the Npgsql default is 100, which one service can use up on its own). Size it so that pool x replicas, summed over the services that share a server, stays below the server's max_connections with headroom. A pool below what a service needs under load slows it down instead of protecting it: locally the order service held about 80 connections at 500 requests/s, and capping it at 40 cut the sustainable rate by a quarter. Not yet measured on Azure.')
@minValue(5)
param postgresMaxPoolSize int = 50

param redisSkuName string = 'Balanced_B0'
param redisPersistence bool = false

// ────────────────────────── platform / registry / vault ───────────────────────

@allowed(['Basic', 'Standard', 'Premium'])
param acrSku string = 'Basic'

param keyVaultPurgeProtection bool = false
param logRetentionDays int = 30

// ───────────────────────────────── application ────────────────────────────────

@description('Per-app sizing. `default` applies to every app; add an app name to override individual fields.')
param scale object = {
  default: {
    cpu: '0.5'
    memory: '1Gi'
    minReplicas: 0
    maxReplicas: 3
  }
}

param jwtIssuer string = 'TaskManager'
param jwtAudience string = 'TaskManager'

@description('Frontend origins the gateway allows (Cors__AllowedOrigins).')
param corsAllowedOrigins array = []

param rabbitmqUser string = 'taskmanager'
param adminEmail string
param adminDisplayName string = 'Administrator'

// ─────────────────────────────────── edge ─────────────────────────────────────

param deployApim bool = false

@description('Consumption/Developer/BasicV2/StandardV2/Premium. Private networkMode needs StandardV2, BasicV2, Developer or Premium; Consumption cannot reach a VNet.')
@allowed(['Consumption', 'Developer', 'BasicV2', 'StandardV2', 'Premium'])
param apimSku string = 'StandardV2'

param apimPublisherEmail string = adminEmail
param apimPublisherName string = 'TaskManager'
param apimRateLimitPerMinute int = 600

// ─────────────────────────────── observability ────────────────────────────────

@description('Self-host Prometheus + Grafana. Application Insights cannot take metrics through the Container Apps agent, so this is the metrics path.')
param deployObservabilityStack bool = true

@description('Give Grafana a public endpoint. Ignored (internal only) in private mode.')
param exposeGrafana bool = false

// ────────────────────────────────── secrets ───────────────────────────────────
// Supplied at deploy time (see params/*.bicepparam: readEnvironmentVariable) and
// stored straight into Key Vault. Never committed, never given defaults.

@secure()
param postgresAdminPassword string

@secure()
param jwtKey string

@secure()
param rabbitmqPassword string

@secure()
param adminSeedPassword string

@secure()
param grafanaAdminPassword string

// ═════════════════════════════════════════════════════════════════════════════
// Names and shared values
// ═════════════════════════════════════════════════════════════════════════════

var isPrivate = networkMode == 'private'
var deployMigrations = deployStage != 'foundation'
var deployApps = deployStage == 'apps'
var baseName = '${namePrefix}-${environmentName}'
var unique = uniqueString(resourceGroup().id)
var mergedTags = union(tags, {
  application: 'taskmanager'
  environment: environmentName
})

var acrName = take(toLower(replace('${namePrefix}${environmentName}acr${unique}', '-', '')), 50)
var keyVaultName = take('${baseName}-kv-${unique}', 24)
var storageName = take(toLower(replace('${namePrefix}${environmentName}st${unique}', '-', '')), 24)
var redisName = '${baseName}-redis-${take(unique, 5)}'
var postgresBaseName = '${baseName}-pg-${take(unique, 5)}'
var apimName = '${baseName}-apim-${take(unique, 5)}'
var postgresPrivateZoneName = '${baseName}.private.postgres.database.azure.com'

var rabbitmqShare = 'rabbitmq-data'
var registryHost = acr.outputs.loginServer

// The databases, and the connection-string name each service reads. TaskService
// is intentionally absent from the Azure template.
var databases = [
  { service: 'users-api', name: 'userflow', connectionName: 'UserDatabase' }
  { service: 'catalog-api', name: 'catalogflow', connectionName: 'CatalogDatabase' }
  { service: 'inventory-api', name: 'inventoryflow', connectionName: 'InventoryDatabase' }
  { service: 'order-api', name: 'orderflow', connectionName: 'OrderDatabase' }
]

var postgresServers = postgresTopology == 'shared'
  ? [
      {
        name: postgresBaseName
        databases: map(databases, database => database.name)
      }
    ]
  : map(databases, database => {
      name: '${postgresBaseName}-${database.service}'
      databases: [database.name]
    })

func appScale(scale object, app string) object => union(scale.default, scale[?app] ?? {})

// ═════════════════════════════════════════════════════════════════════════════
// Foundation
// ═════════════════════════════════════════════════════════════════════════════

module monitoring 'modules/monitoring.bicep' = {
  name: 'monitoring'
  params: {
    name: baseName
    location: location
    tags: mergedTags
    retentionInDays: logRetentionDays
  }
}

module identity 'modules/identity.bicep' = {
  name: 'identity'
  params: {
    name: baseName
    location: location
    tags: mergedTags
  }
}

module network 'modules/network.bicep' = if (isPrivate) {
  name: 'network'
  params: {
    name: baseName
    location: location
    tags: mergedTags
    addressPrefix: vnetAddressPrefix
    postgresPrivateZoneName: postgresPrivateZoneName
  }
}

module acr 'modules/acr.bicep' = {
  name: 'acr'
  params: {
    name: acrName
    location: location
    tags: mergedTags
    sku: acrSku
    pullPrincipalId: identity.outputs.principalId
  }
}

module keyVault 'modules/keyvault.bicep' = {
  name: 'keyvault'
  params: {
    name: keyVaultName
    location: location
    tags: mergedTags
    enablePurgeProtection: keyVaultPurgeProtection
    publicNetworkAccess: isPrivate ? 'Disabled' : 'Enabled'
    readerPrincipalId: identity.outputs.principalId
  }
}

module postgres 'modules/postgres.bicep' = [
  for server in postgresServers: {
    name: 'postgres-${server.name}'
    params: {
      name: server.name
      location: location
      tags: mergedTags
      skuName: postgresSkuName
      skuTier: postgresSkuTier
      storageSizeGB: postgresStorageGB
      highAvailabilityMode: postgresHighAvailability
      backupRetentionDays: postgresBackupRetentionDays
      geoRedundantBackup: postgresGeoRedundantBackup
      administratorLogin: postgresAdminLogin
      administratorPassword: postgresAdminPassword
      databaseNames: server.databases
      delegatedSubnetId: isPrivate ? network!.outputs.postgresSubnetId : ''
      privateDnsZoneId: isPrivate ? network!.outputs.postgresZoneId : ''
    }
  }
]

module redis 'modules/redis.bicep' = {
  name: 'redis'
  params: {
    name: redisName
    location: location
    tags: mergedTags
    skuName: redisSkuName
    publicNetworkAccess: isPrivate ? 'Disabled' : 'Enabled'
    persistenceEnabled: redisPersistence
  }
}

module storage 'modules/storage.bicep' = {
  name: 'storage'
  params: {
    name: storageName
    location: location
    tags: mergedTags
    shareNames: [rabbitmqShare]
    publicNetworkAccess: isPrivate ? 'Disabled' : 'Enabled'
  }
}

// ── private endpoints (private mode only) ────────────────────────────────────

module keyVaultEndpoint 'modules/private-endpoint.bicep' = if (isPrivate) {
  name: 'pe-keyvault'
  params: {
    name: '${baseName}-kv-pe'
    location: location
    tags: mergedTags
    subnetId: network!.outputs.privateEndpointsSubnetId
    targetResourceId: keyVault.outputs.id
    groupId: 'vault'
    privateDnsZoneId: network!.outputs.keyVaultZoneId
  }
}

module redisEndpoint 'modules/private-endpoint.bicep' = if (isPrivate) {
  name: 'pe-redis'
  params: {
    name: '${baseName}-redis-pe'
    location: location
    tags: mergedTags
    subnetId: network!.outputs.privateEndpointsSubnetId
    targetResourceId: redis.outputs.id
    groupId: 'redisEnterprise'
    privateDnsZoneId: network!.outputs.redisZoneId
  }
}

module storageEndpoint 'modules/private-endpoint.bicep' = if (isPrivate) {
  name: 'pe-storage'
  params: {
    name: '${baseName}-st-pe'
    location: location
    tags: mergedTags
    subnetId: network!.outputs.privateEndpointsSubnetId
    targetResourceId: storage.outputs.id
    groupId: 'file'
    privateDnsZoneId: network!.outputs.filesZoneId
  }
}

module appEnvironment 'modules/containerapps-env.bicep' = {
  name: 'containerapps-env'
  params: {
    name: '${baseName}-env'
    location: location
    tags: mergedTags
    workspaceName: monitoring.outputs.workspaceName
    appInsightsName: monitoring.outputs.appInsightsName
    infrastructureSubnetId: isPrivate ? network!.outputs.acaSubnetId : ''
    storageAccountName: storage.outputs.name
    shares: [{ name: rabbitmqShare }]
  }
  dependsOn: [
    storageEndpoint
  ]
}

// ═════════════════════════════════════════════════════════════════════════════
// Secrets -> Key Vault
// ═════════════════════════════════════════════════════════════════════════════

resource vault 'Microsoft.KeyVault/vaults@2024-11-01' existing = {
  name: keyVaultName
}

resource jwtKeySecret 'Microsoft.KeyVault/vaults/secrets@2024-11-01' = {
  parent: vault
  name: 'jwt-key'
  properties: { value: jwtKey }
  dependsOn: [keyVault]
}

resource rabbitmqPasswordSecret 'Microsoft.KeyVault/vaults/secrets@2024-11-01' = {
  parent: vault
  name: 'rabbitmq-password'
  properties: { value: rabbitmqPassword }
  dependsOn: [keyVault]
}

resource adminSeedPasswordSecret 'Microsoft.KeyVault/vaults/secrets@2024-11-01' = {
  parent: vault
  name: 'admin-seed-password'
  properties: { value: adminSeedPassword }
  dependsOn: [keyVault]
}

resource grafanaPasswordSecret 'Microsoft.KeyVault/vaults/secrets@2024-11-01' = {
  parent: vault
  name: 'grafana-admin-password'
  properties: { value: grafanaAdminPassword }
  dependsOn: [keyVault]
}

// Kept for operators (psql, pgAdmin); the apps use the connection strings below.
resource postgresAdminPasswordSecret 'Microsoft.KeyVault/vaults/secrets@2024-11-01' = {
  parent: vault
  name: 'postgres-admin-password'
  properties: { value: postgresAdminPassword }
  dependsOn: [keyVault]
}

resource redisConnectionSecret 'Microsoft.KeyVault/vaults/secrets@2024-11-01' = {
  parent: vault
  name: 'redis-connection'
  properties: { value: redis.outputs.connectionString }
  dependsOn: [keyVault]
}

resource databaseConnectionSecrets 'Microsoft.KeyVault/vaults/secrets@2024-11-01' = [
  for (database, i) in databases: {
    parent: vault
    name: 'conn-${toLower(database.connectionName)}'
    properties: {
      value: 'Host=${postgres[postgresTopology == 'shared' ? 0 : i].outputs.fqdn};Port=5432;Database=${database.name};Username=${postgresAdminLogin};Password=${postgresAdminPassword};Maximum Pool Size=${postgresMaxPoolSize};${postgresConnectionOptions}'
    }
    dependsOn: [keyVault]
  }
]

// Built from the vault name rather than read from the module output: the app
// loop below needs a value known at the start of the deployment, and an output
// is not. The secret resources above carry the ordering dependency instead.
var secretsUri = 'https://${keyVaultName}${environment().suffixes.keyvaultDns}/secrets/'

func secretRef(uri string, name string) object => {
  name: name
  keyVaultUrl: '${uri}${name}'
}

// ═════════════════════════════════════════════════════════════════════════════
// Applications (phase 2: deployApps = true)
// ═════════════════════════════════════════════════════════════════════════════

var httpProbes = [
  {
    type: 'Startup'
    tcpSocket: { port: 8080 }
    initialDelaySeconds: 2
    periodSeconds: 3
    failureThreshold: 30
  }
  // Readiness follows /health, which includes the database (or Redis) check:
  // an unhealthy dependency takes the replica out of rotation.
  {
    type: 'Readiness'
    httpGet: { path: '/health', port: 8080 }
    periodSeconds: 10
    failureThreshold: 3
  }
  // Liveness is a bare TCP check on purpose. /health depends on the database,
  // so using it for liveness would restart every replica during a database
  // blip and turn a brief outage into a crash loop.
  {
    type: 'Liveness'
    tcpSocket: { port: 8080 }
    periodSeconds: 20
    failureThreshold: 3
  }
]

// The gateway has no /health endpoint, so it gets TCP checks only.
var gatewayProbes = [
  {
    type: 'Startup'
    tcpSocket: { port: 8080 }
    initialDelaySeconds: 2
    periodSeconds: 3
    failureThreshold: 30
  }
  {
    type: 'Readiness'
    tcpSocket: { port: 8080 }
    periodSeconds: 10
    failureThreshold: 3
  }
  {
    type: 'Liveness'
    tcpSocket: { port: 8080 }
    periodSeconds: 20
    failureThreshold: 3
  }
]

var commonEnv = [
  { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
  { name: 'ASPNETCORE_URLS', value: 'http://+:8080' }
]

var jwtEnv = [
  { name: 'Jwt__Key', secretRef: 'jwt-key' }
  { name: 'Jwt__Issuer', value: jwtIssuer }
  { name: 'Jwt__Audience', value: jwtAudience }
]

var busEnv = [
  { name: 'RabbitMq__Host', value: 'rabbitmq' }
  { name: 'RabbitMq__Username', value: rabbitmqUser }
  { name: 'RabbitMq__Password', secretRef: 'rabbitmq-password' }
]

var jwtSecret = secretRef(secretsUri, 'jwt-key')
var busSecret = secretRef(secretsUri, 'rabbitmq-password')

// One entry per database-backed service; drives the four apps below.
var serviceDefinitions = {
  'users-api': {
    connectionName: 'UserDatabase'
    usesBus: true
    extraEnv: [
      { name: 'Admin__Email', value: adminEmail }
      { name: 'Admin__Password', secretRef: 'admin-seed-password' }
      { name: 'Admin__DisplayName', value: adminDisplayName }
    ]
    extraSecrets: [secretRef(secretsUri, 'admin-seed-password')]
  }
  'catalog-api': {
    connectionName: 'CatalogDatabase'
    usesBus: false
    extraEnv: []
    extraSecrets: []
  }
  'inventory-api': {
    connectionName: 'InventoryDatabase'
    usesBus: true
    extraEnv: []
    extraSecrets: []
  }
  'order-api': {
    connectionName: 'OrderDatabase'
    usesBus: true
    extraEnv: [
      // Orders are priced from the catalog (internal ingress, like the gateway's route to it).
      { name: 'Catalog__BaseUrl', value: 'http://catalog-api' }
    ]
    extraSecrets: []
  }
}

module rabbitmq 'modules/containerapp.bicep' = if (deployApps) {
  name: 'app-rabbitmq'
  params: {
    name: 'rabbitmq'
    location: location
    tags: mergedTags
    environmentId: appEnvironment.outputs.id
    identityId: identity.outputs.id
    registryServer: registryHost
    image: '${registryHost}/rabbitmq:${imageTag}'
    ingressMode: 'internal'
    transport: 'tcp'
    targetPort: 5672
    exposedPort: 5672
    additionalPortMappings: [
      { external: false, targetPort: 15692, exposedPort: 15692 } // Prometheus metrics
      { external: false, targetPort: 15672, exposedPort: 15672 } // management UI
    ]
    secrets: [busSecret]
    env: [
      { name: 'RABBITMQ_DEFAULT_USER', value: rabbitmqUser }
      { name: 'RABBITMQ_DEFAULT_PASS', secretRef: 'rabbitmq-password' }
      // The data directory is keyed by node name, which defaults to the
      // container hostname - and that changes on every restart, so the
      // broker would come up as a new node and ignore its own persisted queues.
      { name: 'RABBITMQ_NODENAME', value: 'rabbit@localhost' }
    ]
    cpu: '0.5'
    memory: '1Gi'
    // One replica only: this is a single-node broker, not a cluster.
    minReplicas: 1
    maxReplicas: 1
    probes: [
      {
        type: 'Startup'
        tcpSocket: { port: 5672 }
        initialDelaySeconds: 10
        periodSeconds: 5
        failureThreshold: 30
      }
      {
        type: 'Liveness'
        tcpSocket: { port: 5672 }
        periodSeconds: 30
        failureThreshold: 5
      }
    ]
    volumes: [
      {
        name: 'data'
        storageType: 'AzureFile'
        storageName: rabbitmqShare
        // 0600/0700 owned by rabbitmq (uid 999): Erlang refuses to start if
        // .erlang.cookie is readable by anyone else, and SMB ignores chmod,
        // so the mount options are the only place that permission can be set.
        mountOptions: 'uid=999,gid=999,file_mode=0600,dir_mode=0700,nobrl'
      }
    ]
    volumeMounts: [
      { volumeName: 'data', mountPath: '/var/lib/rabbitmq' }
    ]
  }
  dependsOn: [
    rabbitmqPasswordSecret
  ]
}

module apis 'modules/containerapp.bicep' = [
  for item in items(serviceDefinitions): if (deployApps) {
    name: 'app-${item.key}'
    params: {
      name: item.key
      location: location
      tags: mergedTags
      environmentId: appEnvironment.outputs.id
      identityId: identity.outputs.id
      registryServer: registryHost
      image: '${registryHost}/${item.key}:${imageTag}'
      ingressMode: 'internal'
      allowInsecure: true
      cpu: appScale(scale, item.key).cpu
      memory: appScale(scale, item.key).memory
      minReplicas: appScale(scale, item.key).minReplicas
      maxReplicas: appScale(scale, item.key).maxReplicas
      probes: httpProbes
      secrets: concat(
        [
          jwtSecret
          secretRef(secretsUri, 'conn-${toLower(item.value.connectionName)}')
        ],
        item.value.usesBus ? [busSecret] : [],
        item.value.extraSecrets
      )
      env: concat(
        commonEnv,
        jwtEnv,
        [
          { name: 'ConnectionStrings__${item.value.connectionName}', secretRef: 'conn-${toLower(item.value.connectionName)}' }
        ],
        item.value.usesBus ? busEnv : [],
        item.value.extraEnv
      )
    }
    dependsOn: [
      databaseConnectionSecrets
      jwtKeySecret
      rabbitmqPasswordSecret
      adminSeedPasswordSecret
      rabbitmq
    ]
  }
]

module basket 'modules/containerapp.bicep' = if (deployApps) {
  name: 'app-basket-api'
  params: {
    name: 'basket-api'
    location: location
    tags: mergedTags
    environmentId: appEnvironment.outputs.id
    identityId: identity.outputs.id
    registryServer: registryHost
    image: '${registryHost}/basket-api:${imageTag}'
    ingressMode: 'internal'
    allowInsecure: true
    cpu: appScale(scale, 'basket-api').cpu
    memory: appScale(scale, 'basket-api').memory
    minReplicas: appScale(scale, 'basket-api').minReplicas
    maxReplicas: appScale(scale, 'basket-api').maxReplicas
    probes: httpProbes
    secrets: [jwtSecret, secretRef(secretsUri, 'redis-connection')]
    env: concat(commonEnv, jwtEnv, [
      { name: 'ConnectionStrings__Redis', secretRef: 'redis-connection' }
    ])
  }
  dependsOn: [
    jwtKeySecret
    redisConnectionSecret
    redisEndpoint
  ]
}

// The gateway's routing table is unchanged; only the destinations are pointed at
// the apps' in-environment names. tasks-cluster keeps its default (unreachable)
// address because TaskService is not part of the Azure template.
var gatewayEnv = concat(
  commonEnv,
  [
    { name: 'ReverseProxy__Clusters__users-cluster__Destinations__destination1__Address', value: 'http://users-api/' }
    { name: 'ReverseProxy__Clusters__catalog-cluster__Destinations__destination1__Address', value: 'http://catalog-api/' }
    { name: 'ReverseProxy__Clusters__inventory-cluster__Destinations__destination1__Address', value: 'http://inventory-api/' }
    { name: 'ReverseProxy__Clusters__basket-cluster__Destinations__destination1__Address', value: 'http://basket-api/' }
    { name: 'ReverseProxy__Clusters__order-cluster__Destinations__destination1__Address', value: 'http://order-api/' }
  ],
  map(range(0, length(corsAllowedOrigins)), i => {
    name: 'Cors__AllowedOrigins__${i}'
    value: corsAllowedOrigins[i]
  })
)

module gateway 'modules/containerapp.bicep' = if (deployApps) {
  name: 'app-gateway'
  params: {
    name: 'gateway'
    location: location
    tags: mergedTags
    environmentId: appEnvironment.outputs.id
    identityId: identity.outputs.id
    registryServer: registryHost
    image: '${registryHost}/gateway:${imageTag}'
    // Public in public mode; in private mode the environment itself is internal
    // and API Management (inside the VNet) is the way in.
    ingressMode: isPrivate ? 'internal' : 'external'
    allowedCidrs: isPrivate ? [] : gatewayAllowedCidrs
    cpu: appScale(scale, 'gateway').cpu
    memory: appScale(scale, 'gateway').memory
    minReplicas: appScale(scale, 'gateway').minReplicas
    maxReplicas: appScale(scale, 'gateway').maxReplicas
    probes: gatewayProbes
    env: gatewayEnv
  }
  dependsOn: [
    apis
    basket
  ]
}

// ── metrics: self-hosted Prometheus + Grafana ────────────────────────────────

module prometheus 'modules/containerapp.bicep' = if (deployApps && deployObservabilityStack) {
  name: 'app-prometheus'
  params: {
    name: 'prometheus'
    location: location
    tags: mergedTags
    environmentId: appEnvironment.outputs.id
    identityId: identity.outputs.id
    registryServer: registryHost
    image: '${registryHost}/prometheus:${imageTag}'
    targetPort: 9090
    ingressMode: 'internal'
    allowInsecure: true
    cpu: '0.5'
    memory: '1Gi'
    // Scraping a load-balanced app name reaches one replica at random, so the
    // scraper itself must be a single instance, and its TSDB is ephemeral.
    minReplicas: 1
    maxReplicas: 1
    probes: [
      {
        type: 'Liveness'
        httpGet: { path: '/-/healthy', port: 9090 }
        periodSeconds: 20
        failureThreshold: 3
      }
    ]
  }
  dependsOn: [
    apis
    basket
    rabbitmq
  ]
}

module grafana 'modules/containerapp.bicep' = if (deployApps && deployObservabilityStack) {
  name: 'app-grafana'
  params: {
    name: 'grafana'
    location: location
    tags: mergedTags
    environmentId: appEnvironment.outputs.id
    identityId: identity.outputs.id
    registryServer: registryHost
    image: '${registryHost}/grafana:${imageTag}'
    targetPort: 3000
    ingressMode: exposeGrafana && !isPrivate ? 'external' : 'internal'
    allowInsecure: !(exposeGrafana && !isPrivate)
    cpu: '0.5'
    memory: '1Gi'
    minReplicas: 1
    maxReplicas: 1
    secrets: [secretRef(secretsUri, 'grafana-admin-password')]
    env: [
      { name: 'GF_SECURITY_ADMIN_PASSWORD', secretRef: 'grafana-admin-password' }
      { name: 'GF_USERS_ALLOW_SIGN_UP', value: 'false' }
    ]
    probes: [
      {
        type: 'Liveness'
        httpGet: { path: '/api/health', port: 3000 }
        periodSeconds: 30
        failureThreshold: 3
      }
    ]
  }
  dependsOn: [
    grafanaPasswordSecret
    prometheus
  ]
}

// ── migrations ───────────────────────────────────────────────────────────────

// Deployed one stage BEFORE the apps, so the databases are migrated before any
// service exists to start against an empty schema.
module migrations 'modules/migration-job.bicep' = if (deployMigrations) {
  name: 'job-migrations'
  params: {
    name: 'migrations'
    location: location
    tags: mergedTags
    environmentId: appEnvironment.outputs.id
    identityId: identity.outputs.id
    registryServer: registryHost
    image: '${registryHost}/migrations:${imageTag}'
    secrets: [for database in databases: secretRef(secretsUri, 'conn-${toLower(database.connectionName)}')]
    env: [
      for database in databases: {
        name: 'ConnectionStrings__${database.connectionName}'
        secretRef: 'conn-${toLower(database.connectionName)}'
      }
    ]
  }
  dependsOn: [
    databaseConnectionSecrets
  ]
}

// ── edge ─────────────────────────────────────────────────────────────────────

module apim 'modules/apim.bicep' = if (deployApps && deployApim) {
  name: 'apim'
  params: {
    name: apimName
    location: location
    tags: mergedTags
    skuName: apimSku
    publisherEmail: apimPublisherEmail
    publisherName: apimPublisherName
    gatewayUrl: 'https://${gateway!.outputs.fqdn}'
    vnetIntegrationSubnetId: isPrivate ? network!.outputs.apimSubnetId : ''
    rateLimitCallsPerMinute: apimRateLimitPerMinute
  }
}

// ═════════════════════════════════════════════════════════════════════════════
// Outputs consumed by scripts/deploy-azure.sh
// ═════════════════════════════════════════════════════════════════════════════

output acrName string = acr.outputs.name
output acrLoginServer string = acr.outputs.loginServer
output keyVaultName string = keyVault.outputs.name
output containerAppsEnvironment string = appEnvironment.outputs.name
output containerAppsDomain string = appEnvironment.outputs.defaultDomain
output migrationJobName string = 'migrations'
output gatewayFqdn string = deployApps ? gateway!.outputs.fqdn : ''
output apimGatewayUrl string = deployApps && deployApim ? apim!.outputs.gatewayUrl : ''
output grafanaFqdn string = deployApps && deployObservabilityStack ? grafana!.outputs.fqdn : ''
