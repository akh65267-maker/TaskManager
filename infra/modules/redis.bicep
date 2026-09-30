// Azure Managed Redis for BasketService.
//
// Not "Azure Cache for Redis": Basic/Standard/Premium is retiring on
// 2028-09-30 and creation is already blocked for new customers, with Azure
// Managed Redis as the successor. BasketService needs only GET/SET on a
// per-user key, which any tier serves.

param name string
param location string
param tags object

// Balanced_B0 is the smallest tier. Larger tiers are a parameter change.
param skuName string = 'Balanced_B0'

@allowed(['Enabled', 'Disabled'])
param publicNetworkAccess string = 'Enabled'

// Baskets have no TTL and live only in Redis, so without persistence a restart
// silently empties every customer's basket (compose behaves the same way).
param persistenceEnabled bool = false

resource cluster 'Microsoft.Cache/redisEnterprise@2025-07-01' = {
  name: name
  location: location
  tags: tags
  sku: {
    name: skuName
  }
  properties: {
    minimumTlsVersion: '1.2'
    publicNetworkAccess: publicNetworkAccess
  }
}

resource database 'Microsoft.Cache/redisEnterprise/databases@2025-07-01' = {
  parent: cluster
  name: 'default'
  properties: {
    clientProtocol: 'Encrypted'
    port: 10000
    // Enterprise policy exposes one endpoint that behaves like a standalone
    // server, so nothing about the client has to be cluster-aware.
    clusteringPolicy: 'EnterpriseCluster'
    // NoEviction: a full cache should refuse writes, not quietly evict baskets.
    evictionPolicy: 'NoEviction'
    accessKeysAuthentication: 'Enabled'
    persistence: persistenceEnabled
      ? {
          aofEnabled: true
          aofFrequency: '1s'
        }
      : {}
  }
}

output id string = cluster.id
output hostName string = cluster.properties.hostName
output port int = database.properties.port

// StackExchange.Redis connection string. Assembled here so the access key never
// leaves the deployment except into Key Vault (see main.bicep).
#disable-next-line outputs-should-not-contain-secrets
output connectionString string = '${cluster.properties.hostName}:${database.properties.port},password=${database.listKeys().primaryKey},ssl=True,abortConnect=False'
