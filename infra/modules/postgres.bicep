// One PostgreSQL Flexible Server and the databases it hosts. main.bicep calls
// this once for the whole system ('shared' topology) or once per service
// ('perService'), so the same module serves both.

param name string
param location string
param tags object

param version string = '17'
param skuName string = 'Standard_B1ms'

@allowed(['Burstable', 'GeneralPurpose', 'MemoryOptimized'])
param skuTier string = 'Burstable'

param storageSizeGB int = 32

@allowed(['Disabled', 'ZoneRedundant', 'SameZone'])
param highAvailabilityMode string = 'Disabled'

param backupRetentionDays int = 7
param geoRedundantBackup bool = false

param administratorLogin string

@secure()
param administratorPassword string

param databaseNames array

// Private mode: the server joins a delegated subnet and is reachable only from
// the VNet. Leave both empty for public mode.
param delegatedSubnetId string = ''
param privateDnsZoneId string = ''

// Public mode only. The 0.0.0.0 rule admits every Azure service in every
// tenant, not just this environment - convenient for dev, and the reason
// staging/prod should use networkMode 'private'.
param allowAzureServices bool = true

var isPrivate = !empty(delegatedSubnetId)

resource server 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' = {
  name: name
  location: location
  tags: tags
  sku: {
    name: skuName
    tier: skuTier
  }
  properties: {
    version: version
    administratorLogin: administratorLogin
    administratorLoginPassword: administratorPassword
    storage: {
      storageSizeGB: storageSizeGB
    }
    backup: {
      backupRetentionDays: backupRetentionDays
      geoRedundantBackup: geoRedundantBackup ? 'Enabled' : 'Disabled'
    }
    highAvailability: {
      mode: highAvailabilityMode
    }
    authConfig: {
      passwordAuth: 'Enabled'
      activeDirectoryAuth: 'Disabled'
    }
    network: isPrivate
      ? {
          delegatedSubnetResourceId: delegatedSubnetId
          privateDnsZoneArmResourceId: privateDnsZoneId
          publicNetworkAccess: 'Disabled'
        }
      : {
          publicNetworkAccess: 'Enabled'
        }
  }
}

resource databases 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2024-08-01' = [
  for database in databaseNames: {
    parent: server
    name: database
    properties: {
      charset: 'UTF8'
      collation: 'en_US.utf8'
    }
  }
]

resource azureServices 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2024-08-01' = if (!isPrivate && allowAzureServices) {
  parent: server
  name: 'AllowAllAzureServicesAndResourcesWithinAzureIps'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
  dependsOn: [
    databases
  ]
}

output fqdn string = server.properties.fullyQualifiedDomainName
