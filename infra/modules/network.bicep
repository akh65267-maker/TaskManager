// VNet, delegated subnets and private DNS zones for networkMode = 'private'.
// Not deployed at all in 'public' mode.

param name string
param location string
param tags object

param addressPrefix string = '10.40.0.0/16'

// Fixed suffix rather than a parameter: Postgres requires a zone under
// *.postgres.database.azure.com, and the server name is unique per env already.
param postgresPrivateZoneName string

// Laid out for the default 10.40.0.0/16; each block is checked not to overlap
// the others by the subnet index arithmetic below (index * block size = offset).
var subnets = {
  aca: cidrSubnet(addressPrefix, 23, 0) // 10.40.0.0/23  - Container Apps environment
  postgres: cidrSubnet(addressPrefix, 28, 32) // 10.40.2.0/28  - offset 512
  privateEndpoints: cidrSubnet(addressPrefix, 27, 17) // 10.40.2.32/27 - offset 544
  apim: cidrSubnet(addressPrefix, 27, 18) // 10.40.2.64/27 - offset 576, APIM VNet integration
}

// Zones for the private endpoints (Key Vault, Azure Files, Managed Redis).
// ACR is deliberately absent: it stays publicly reachable (Entra-authenticated,
// pulled by managed identity) so CI and `az acr build` can still push to it.
var privateEndpointZones = [
  'privatelink.vaultcore.azure.net'
  'privatelink.file.${environment().suffixes.storage}'
  'privatelink.redis.azure.net'
]

resource vnet 'Microsoft.Network/virtualNetworks@2024-05-01' = {
  name: '${name}-vnet'
  location: location
  tags: tags
  properties: {
    addressSpace: {
      addressPrefixes: [addressPrefix]
    }
    subnets: [
      {
        name: 'aca'
        properties: {
          addressPrefix: subnets.aca
          delegations: [
            {
              name: 'aca'
              properties: {
                serviceName: 'Microsoft.App/environments'
              }
            }
          ]
        }
      }
      {
        name: 'postgres'
        properties: {
          addressPrefix: subnets.postgres
          delegations: [
            {
              name: 'postgres'
              properties: {
                serviceName: 'Microsoft.DBforPostgreSQL/flexibleServers'
              }
            }
          ]
        }
      }
      {
        name: 'private-endpoints'
        properties: {
          addressPrefix: subnets.privateEndpoints
        }
      }
      {
        name: 'apim'
        properties: {
          addressPrefix: subnets.apim
          delegations: [
            {
              name: 'apim'
              properties: {
                serviceName: 'Microsoft.Web/serverFarms'
              }
            }
          ]
        }
      }
    ]
  }
}

resource postgresZone 'Microsoft.Network/privateDnsZones@2024-06-01' = {
  name: postgresPrivateZoneName
  location: 'global'
  tags: tags
}

resource postgresZoneLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = {
  parent: postgresZone
  name: '${name}-link'
  location: 'global'
  properties: {
    registrationEnabled: false
    virtualNetwork: {
      id: vnet.id
    }
  }
}

resource zones 'Microsoft.Network/privateDnsZones@2024-06-01' = [
  for zone in privateEndpointZones: {
    name: zone
    location: 'global'
    tags: tags
  }
]

resource zoneLinks 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = [
  for (zone, i) in privateEndpointZones: {
    parent: zones[i]
    name: '${name}-link'
    location: 'global'
    properties: {
      registrationEnabled: false
      virtualNetwork: {
        id: vnet.id
      }
    }
  }
]

output acaSubnetId string = '${vnet.id}/subnets/aca'
output postgresSubnetId string = '${vnet.id}/subnets/postgres'
output privateEndpointsSubnetId string = '${vnet.id}/subnets/private-endpoints'
output apimSubnetId string = '${vnet.id}/subnets/apim'
output postgresZoneId string = postgresZone.id
output keyVaultZoneId string = zones[0].id
output filesZoneId string = zones[1].id
output redisZoneId string = zones[2].id
