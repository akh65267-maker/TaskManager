// Container Apps environment: the shared network/logging boundary for every app.

param name string
param location string
param tags object

param workspaceName string
param appInsightsName string

// Empty = public environment. When set, the environment is VNet-injected and
// internal-only: nothing in it is reachable from the internet except through
// something that itself sits inside the VNet (API Management).
param infrastructureSubnetId string = ''

param storageAccountName string = ''

// Azure Files shares to expose to apps in this environment, as {name} objects.
param shares array = []

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' existing = {
  name: workspaceName
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: appInsightsName
}

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' existing = if (!empty(storageAccountName)) {
  name: storageAccountName
}

resource environment 'Microsoft.App/managedEnvironments@2024-10-02-preview' = {
  name: name
  location: location
  tags: tags
  properties: {
    // Console logs (Serilog) go here automatically.
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: workspace.properties.customerId
        sharedKey: workspace.listKeys().primarySharedKey
      }
    }
    // Managed OpenTelemetry agent: traces only. Application Insights does not
    // accept metrics through the agent, and the apps do not emit OTLP logs
    // (Serilog writes to the console), so sending logs here would only
    // duplicate what Log Analytics already has. Each app receives
    // OTEL_EXPORTER_OTLP_ENDPOINT/PROTOCOL injected, which is exactly what the
    // shared Observability library reads - no application change needed.
    appInsightsConfiguration: {
      connectionString: appInsights.properties.ConnectionString
    }
    openTelemetryConfiguration: {
      tracesConfiguration: {
        destinations: ['appInsights']
      }
    }
    vnetConfiguration: empty(infrastructureSubnetId)
      ? null
      : {
          infrastructureSubnetId: infrastructureSubnetId
          internal: true
        }
    workloadProfiles: [
      {
        name: 'Consumption'
        workloadProfileType: 'Consumption'
      }
    ]
  }
}

resource storages 'Microsoft.App/managedEnvironments/storages@2024-10-02-preview' = [
  for share in shares: {
    parent: environment
    name: share.name
    properties: {
      azureFile: {
        accountName: storageAccountName
        accountKey: storageAccount!.listKeys().keys[0].value
        shareName: share.name
        accessMode: 'ReadWrite'
      }
    }
  }
]

output id string = environment.id
output name string = environment.name
output defaultDomain string = environment.properties.defaultDomain
