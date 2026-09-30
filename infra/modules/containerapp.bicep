// Generic container app. Every service, the broker and the observability stack
// use this one module, so ingress, identity, probes and scaling are described
// once. Behaviour is chosen by parameters, not by copy-pasted resources.

param name string
param location string
param tags object

param environmentId string
param identityId string
param registryServer string
param image string

param targetPort int = 8080

// external = public internet; internal = reachable only inside the environment
// (by app name); none = no ingress at all (a background-only app).
@allowed(['external', 'internal', 'none'])
param ingressMode string = 'internal'

@allowed(['auto', 'http', 'http2', 'tcp'])
param transport string = 'auto'

// TCP ingress only.
param exposedPort int = 0
param additionalPortMappings array = []

// Internal HTTP traffic between apps arrives as plain HTTP on port 80; without
// this, ingress answers it with a redirect to HTTPS that YARP/Prometheus won't
// follow. Never set it on an external app.
param allowInsecure bool = false

// Source CIDRs allowed to reach an external app; empty = anywhere.
param allowedCidrs array = []

// [{ name, value }] or [{ name, secretRef }]
param env array = []

// [{ name, keyVaultUrl }] - resolved with the shared managed identity.
param secrets array = []

param cpu string = '0.5'
param memory string = '1Gi'
param minReplicas int = 0
param maxReplicas int = 3

// Overrides the default HTTP-concurrency rule when a service needs another.
param scaleRules array = []
param httpConcurrentRequests int = 50

// Fully formed ACA probe objects; see main.bicep for the per-service choice.
param probes array = []

param volumes array = []
param volumeMounts array = []

var hasIngress = ingressMode != 'none'
var isTcp = transport == 'tcp'

var defaultScaleRules = hasIngress && !isTcp
  ? [
      {
        name: 'http'
        http: {
          metadata: {
            concurrentRequests: string(httpConcurrentRequests)
          }
        }
      }
    ]
  : []

resource app 'Microsoft.App/containerApps@2024-03-01' = {
  name: name
  location: location
  tags: union(tags, { 'app-role': name })
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
      activeRevisionsMode: 'Single'
      ingress: hasIngress
        ? {
            external: ingressMode == 'external'
            targetPort: targetPort
            transport: transport
            exposedPort: isTcp ? exposedPort : null
            allowInsecure: isTcp ? null : allowInsecure
            additionalPortMappings: empty(additionalPortMappings) ? null : additionalPortMappings
            ipSecurityRestrictions: empty(allowedCidrs)
              ? null
              : map(allowedCidrs, (cidr, i) => {
                  name: 'allow-${i}'
                  action: 'Allow'
                  ipAddressRange: cidr
                })
          }
        : null
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
          name: name
          image: image
          env: env
          probes: probes
          volumeMounts: volumeMounts
          resources: {
            cpu: json(cpu)
            memory: memory
          }
        }
      ]
      volumes: volumes
      scale: {
        minReplicas: minReplicas
        maxReplicas: maxReplicas
        rules: empty(scaleRules) ? defaultScaleRules : scaleRules
      }
    }
  }
}

output id string = app.id
output name string = app.name
output fqdn string = hasIngress ? app.properties.configuration.ingress.fqdn : ''
