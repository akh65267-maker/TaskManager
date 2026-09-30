// Azure Container Registry. Images are pulled by managed identity (AcrPull);
// the admin user stays disabled so there is no registry password to leak.

param name string
param location string
param tags object

@allowed(['Basic', 'Standard', 'Premium'])
param sku string = 'Basic'

// Private endpoints require Premium; the caller passes 'Disabled' only when a
// private endpoint fronts the registry.
@allowed(['Enabled', 'Disabled'])
param publicNetworkAccess string = 'Enabled'

param pullPrincipalId string

// Built-in role: AcrPull
var acrPullRoleId = '7f951dda-4ed3-4680-a7ca-43fe172d538d'

resource registry 'Microsoft.ContainerRegistry/registries@2023-11-01-preview' = {
  name: name
  location: location
  tags: tags
  sku: {
    name: sku
  }
  properties: {
    adminUserEnabled: false
    anonymousPullEnabled: false
    publicNetworkAccess: publicNetworkAccess
  }
}

resource pull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: registry
  name: guid(registry.id, pullPrincipalId, acrPullRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRoleId)
    principalId: pullPrincipalId
    principalType: 'ServicePrincipal'
  }
}

output id string = registry.id
output name string = registry.name
output loginServer string = registry.properties.loginServer
