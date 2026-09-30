// Storage account that exists for one reason: an Azure Files share for
// RabbitMQ's data directory. There is no blob or object storage requirement
// anywhere in the application.
//
// Prometheus and Grafana are deliberately not given shares. Prometheus' TSDB
// is unsupported on SMB/NFS, and Grafana is provisioned entirely from files
// baked into its image.

param name string
param location string
param tags object

@allowed(['Standard_LRS', 'Standard_ZRS'])
param skuName string = 'Standard_LRS'

param shareNames array = []

@allowed(['Enabled', 'Disabled'])
param publicNetworkAccess string = 'Enabled'

resource account 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: name
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: {
    name: skuName
  }
  properties: {
    minimumTlsVersion: 'TLS1_2'
    publicNetworkAccess: publicNetworkAccess
    supportsHttpsTrafficOnly: true
    allowBlobPublicAccess: false
    // Container Apps mounts Azure Files with the account key, so shared-key
    // access has to stay on for this account.
    allowSharedKeyAccess: true
  }
}

resource fileService 'Microsoft.Storage/storageAccounts/fileServices@2023-05-01' = {
  parent: account
  name: 'default'
}

resource shares 'Microsoft.Storage/storageAccounts/fileServices/shares@2023-05-01' = [
  for share in shareNames: {
    parent: fileService
    name: share
    properties: {
      shareQuota: 5
    }
  }
]

output id string = account.id
output name string = account.name
