// API Management in front of the existing YARP gateway.
//
// APIM adds the edge concerns the gateway deliberately lacks (throttling, a
// single public hostname, later a WAF/front door) without touching the
// gateway's routing table, which stays the one place routes are defined.
//
// CORS is intentionally NOT configured here. The gateway already answers CORS
// from Cors:AllowedOrigins; adding a second CORS policy would emit duplicate
// Access-Control-Allow-Origin headers, which browsers reject.

param name string
param location string
param tags object

@allowed(['Consumption', 'Developer', 'BasicV2', 'StandardV2', 'Premium'])
param skuName string = 'StandardV2'

param publisherEmail string
param publisherName string

// https://<gateway fqdn>
param gatewayUrl string

// StandardV2/BasicV2 only: delegated subnet for outbound VNet integration, so
// APIM can reach an internal Container Apps environment.
param vnetIntegrationSubnetId string = ''

// 0 disables the limit. Ignored on Consumption, which has no per-key limiter.
param rateLimitCallsPerMinute int = 600

var isConsumption = skuName == 'Consumption'
var methods = ['GET', 'POST', 'PUT', 'PATCH', 'DELETE', 'OPTIONS', 'HEAD']

resource service 'Microsoft.ApiManagement/service@2024-05-01' = {
  name: name
  location: location
  tags: tags
  sku: {
    name: skuName
    capacity: isConsumption ? 0 : 1
  }
  properties: {
    publisherEmail: publisherEmail
    publisherName: publisherName
    virtualNetworkType: empty(vnetIntegrationSubnetId) ? 'None' : 'External'
    virtualNetworkConfiguration: empty(vnetIntegrationSubnetId)
      ? null
      : {
          subnetResourceId: vnetIntegrationSubnetId
        }
  }
}

resource backend 'Microsoft.ApiManagement/service/backends@2024-05-01' = {
  parent: service
  name: 'gateway'
  properties: {
    url: gatewayUrl
    protocol: 'http'
  }
}

resource api 'Microsoft.ApiManagement/service/apis@2024-05-01' = {
  parent: service
  name: 'taskmanager'
  properties: {
    displayName: 'TaskManager API'
    path: ''
    protocols: ['https']
    // The storefront calls this anonymously; authentication is the services' JWT.
    subscriptionRequired: false
    serviceUrl: gatewayUrl
  }
}

resource operations 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = [
  for method in methods: {
    parent: api
    name: '${toLower(method)}-all'
    properties: {
      displayName: '${method} *'
      method: method
      urlTemplate: '/*'
    }
  }
]

var policyHead = '''
<policies>
  <inbound>
    <base />
    <!-- Every service publishes an unauthenticated /metrics; never route it. -->
    <choose>
      <when condition="@(context.Request.Url.Path.StartsWith(&quot;/metrics&quot;, StringComparison.OrdinalIgnoreCase))">
        <return-response>
          <set-status code="404" reason="Not Found" />
        </return-response>
      </when>
    </choose>

'''

var policyRateLimit = isConsumption || rateLimitCallsPerMinute <= 0
  ? ''
  : '    <rate-limit-by-key calls="${rateLimitCallsPerMinute}" renewal-period="60" counter-key="@(context.Request.IpAddress)" />\n'

var policyTail = '''
    <set-backend-service backend-id="gateway" />
  </inbound>
  <backend>
    <base />
  </backend>
  <outbound>
    <base />
  </outbound>
  <on-error>
    <base />
  </on-error>
</policies>
'''

resource policy 'Microsoft.ApiManagement/service/apis/policies@2024-05-01' = {
  parent: api
  name: 'policy'
  properties: {
    format: 'rawxml'
    value: '${policyHead}${policyRateLimit}${policyTail}'
  }
  dependsOn: [
    backend
    operations
  ]
}

output name string = service.name
output gatewayUrl string = service.properties.gatewayUrl
