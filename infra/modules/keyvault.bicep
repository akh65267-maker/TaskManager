// Key Vault in RBAC mode. Container apps read secrets through the shared
// managed identity (Key Vault Secrets User) - no access policies, no keys.

param name string
param location string
param tags object

// Purge protection cannot be switched off once on, so it is opt-in: leave it
// off in dev where vaults are torn down and recreated, on in prod.
param enablePurgeProtection bool = false

@allowed(['Enabled', 'Disabled'])
param publicNetworkAccess string = 'Enabled'

param readerPrincipalId string

// Built-in role: Key Vault Secrets User
var secretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'

resource vault 'Microsoft.KeyVault/vaults@2024-11-01' = {
  name: name
  location: location
  tags: tags
  properties: {
    tenantId: tenant().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
    enablePurgeProtection: enablePurgeProtection ? true : null
    publicNetworkAccess: publicNetworkAccess
    networkAcls: {
      defaultAction: 'Allow'
      bypass: 'AzureServices'
    }
  }
}

resource reader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: vault
  name: guid(vault.id, readerPrincipalId, secretsUserRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', secretsUserRoleId)
    principalId: readerPrincipalId
    principalType: 'ServicePrincipal'
  }
}

output id string = vault.id
output name string = vault.name
output uri string = vault.properties.vaultUri
