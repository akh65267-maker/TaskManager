// Log Analytics + workspace-based Application Insights.
//
// Container Apps ships every container's stdout/stderr to Log Analytics on its
// own, so Serilog's console output needs no wiring. Application Insights
// receives traces only, via the environment's managed OpenTelemetry agent (see
// containerapps-env.bicep).

param name string
param location string
param tags object
param retentionInDays int = 30

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${name}-law'
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: retentionInDays
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${name}-appi'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id
    IngestionMode: 'LogAnalytics'
  }
}

output workspaceName string = workspace.name
output appInsightsName string = appInsights.name
