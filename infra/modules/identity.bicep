// One user-assigned managed identity shared by every container app.
//
// It is user-assigned (not system-assigned per app) for a practical reason:
// each app's Key Vault secret references and ACR pull must resolve at the
// moment the app is created, and a system-assigned identity does not exist
// until then - so its role assignments cannot exist first. A pre-created
// identity has its roles in place before any app starts.

param name string
param location string
param tags object

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${name}-id'
  location: location
  tags: tags
}

output id string = identity.id
output principalId string = identity.properties.principalId
output clientId string = identity.properties.clientId
