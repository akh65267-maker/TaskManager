// One-shot job that applies every service's EF Core migrations.
//
// No service applies migrations at startup (there is no Database.Migrate()
// anywhere in src/), so a fresh Azure database is empty and the services fail
// on first query. This job is the deployment-time equivalent of running
// `dotnet ef database update` by hand. It is manual-trigger on purpose:
// migrations must run to completion before the new app revisions start, and
// deploy-azure.sh starts it and waits.

param name string
param location string
param tags object

param environmentId string
param identityId string
param registryServer string
param image string

param env array = []
param secrets array = []

resource job 'Microsoft.App/jobs@2024-03-01' = {
  name: name
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identityId}': {}
    }
  }
  properties: {
    environmentId: environmentId
    workloadProfileName: 'Consumption'
    configuration: {
      triggerType: 'Manual'
      replicaTimeout: 1800
      replicaRetryLimit: 0
      manualTriggerConfig: {
        parallelism: 1
        replicaCompletionCount: 1
      }
      registries: [
        {
          server: registryServer
          identity: identityId
        }
      ]
      secrets: map(secrets, secret => {
        name: secret.name
        keyVaultUrl: secret.keyVaultUrl
        identity: identityId
      })
    }
    template: {
      containers: [
        {
          name: 'migrate'
          image: image
          env: env
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
        }
      ]
    }
  }
}

output name string = job.name
