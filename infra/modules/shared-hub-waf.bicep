@description('Independent hub network name.')
param name string
@description('Actually reviewed regional binding; global-peering PE backends are rejected.')
param location string
@description('Dedicated delegated subnet; private-only isolation feature must be observed Registered before apply.')
param subnetId string
@description('Explicit allocated static private frontend address.')
param privateIp string
@description('Reviewed fixed WAF capacity, without implicit autoscale or cost approval.')
@minValue(1)
@maxValue(125)
param instanceCount int
@description('Hub-owned certificate retrieval identity and vault; independent of either consumer cleanup.')
param dependencies object
@description('Verified existing vault and per-consumer versioned-secret name bindings.')
param resourceNames object
@description('Actual source/readback-bound human HTTPS backend bindings, one per consumer.')
@minLength(1)
@maxLength(2)
param consumers array
@description('Exact shared P2S address pool, not application authorization.')
param vpnClientPrefix string
@description('Hub ownership and unchanged expiry.')
param tags object

var gatewayName = 'agw-${name}'
var wafId = resourceId('Microsoft.Network/applicationGateways', gatewayName)
var roots = filter(consumers, item => item.web.backendRootCertificate != null)
var scrubbing = [for variable in ['RequestIPAddress', 'RequestHeaderNames', 'RequestCookieNames', 'RequestArgNames', 'RequestPostArgNames', 'RequestJSONArgNames']: {
  matchVariable: variable
  selectorMatchOperator: 'EqualsAny'
  selector: '*'
  state: 'Enabled'
}]

resource vault 'Microsoft.KeyVault/vaults@2024-11-01' existing = {
  name: resourceNames.certificateVault
}
resource certificates 'Microsoft.KeyVault/vaults/secrets@2024-11-01' existing = [for consumer in consumers: {
  parent: vault
  name: resourceNames.certificates[consumer.key]
}]
resource certificateGrants 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for (consumer, i) in consumers: {
  name: guid(wafId, consumer.bindingHash, 'certificate-read')
  scope: certificates[i]
  properties: {
    principalId: dependencies.wafIdentityPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
  }
}]

resource policies 'Microsoft.Network/ApplicationGatewayWebApplicationFirewallPolicies@2024-07-01' = [for consumer in consumers: {
  name: 'wafp-${name}-${consumer.key}'
  location: location
  tags: union(tags, { cohorsConsumer: consumer.bindingHash })
  properties: {
    customRules: [
      {
        name: 'RejectNonVpnSource'
        priority: 1
        ruleType: 'MatchRule'
        action: 'Block'
        state: 'Enabled'
        matchConditions: [
          {
            matchVariables: [{ variableName: 'RemoteAddr' }]
            operator: 'IPMatch'
            negationConditon: true
            matchValues: [vpnClientPrefix]
          }
        ]
      }
    ]
    managedRules: {
      exclusions: []
      managedRuleSets: [{ ruleSetType: 'OWASP', ruleSetVersion: '3.2', ruleGroupOverrides: [] }]
    }
    policySettings: {
      state: 'Enabled'
      mode: 'Prevention'
      requestBodyCheck: true
      requestBodyEnforcement: true
      maxRequestBodySizeInKb: consumer.web.limits.bodyKb
      requestBodyInspectLimitInKB: consumer.web.limits.bodyKb
      fileUploadEnforcement: true
      fileUploadLimitInMb: consumer.web.limits.uploadMb
      logScrubbing: { state: 'Enabled', scrubbingRules: scrubbing }
    }
  }
}]

resource gateway 'Microsoft.Network/applicationGateways@2025-03-01' = {
  name: 'agw-${name}'
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${dependencies.wafIdentityId}': {} }
  }
  properties: {
    sku: { name: 'WAF_v2', tier: 'WAF_v2', capacity: instanceCount }
    enableHttp2: false
    globalConfiguration: { enableRequestBuffering: true, enableResponseBuffering: true }
    sslPolicy: { policyType: 'Predefined', policyName: 'AppGwSslPolicy20220101S' }
    gatewayIPConfigurations: [{ name: 'private-isolated', properties: { subnet: { id: subnetId } } }]
    frontendIPConfigurations: [
      {
        name: 'private-only'
        properties: { privateIPAllocationMethod: 'Static', privateIPAddress: privateIp, subnet: { id: subnetId } }
      }
    ]
    frontendPorts: [{ name: 'https', properties: { port: 443 } }]
    sslCertificates: [for consumer in consumers: {
      name: 'cert-${consumer.key}'
      properties: { keyVaultSecretId: consumer.web.certificateSecretId }
    }]
    trustedRootCertificates: [for consumer in roots: {
      name: 'root-${consumer.key}'
      properties: { data: consumer.web.backendRootCertificate }
    }]
    httpListeners: [for (consumer, i) in consumers: {
      name: 'https-${consumer.key}'
      properties: {
        protocol: 'Https'
        hostNames: [consumer.web.frontendHost]
        requireServerNameIndication: true
        frontendIPConfiguration: { id: resourceId('Microsoft.Network/applicationGateways/frontendIPConfigurations', gatewayName, 'private-only') }
        frontendPort: { id: resourceId('Microsoft.Network/applicationGateways/frontendPorts', gatewayName, 'https') }
        sslCertificate: { id: resourceId('Microsoft.Network/applicationGateways/sslCertificates', gatewayName, 'cert-${consumer.key}') }
        firewallPolicy: { id: policies[i].id }
      }
    }]
    backendAddressPools: [for consumer in consumers: {
      name: 'backend-${consumer.key}'
      properties: { backendAddresses: [{ ipAddress: consumer.web.backendPrivateIp }] }
    }]
    backendHttpSettingsCollection: [for consumer in consumers: {
      name: 'https-${consumer.key}'
      properties: {
        protocol: 'Https'
        port: 443
        cookieBasedAffinity: 'Disabled'
        hostName: consumer.web.backendFqdn
        sniName: consumer.web.backendFqdn
        validateSNI: true
        validateCertChainAndExpiry: true
        pickHostNameFromBackendAddress: false
        probeEnabled: true
        probe: { id: resourceId('Microsoft.Network/applicationGateways/probes', gatewayName, 'ready-${consumer.key}') }
        requestTimeout: consumer.web.limits.requestTimeoutSeconds
        connectionDraining: { enabled: true, drainTimeoutInSec: consumer.web.limits.drainSeconds }
        trustedRootCertificates: consumer.web.backendRootCertificate == null ? [] : [{ id: resourceId('Microsoft.Network/applicationGateways/trustedRootCertificates', gatewayName, 'root-${consumer.key}') }]
      }
    }]
    probes: [for consumer in consumers: {
      name: 'ready-${consumer.key}'
      properties: {
        protocol: 'Https'
        host: consumer.web.probe.host
        path: consumer.web.probe.path
        port: consumer.web.probe.port
        pickHostNameFromBackendHttpSettings: false
        interval: 30
        timeout: 10
        unhealthyThreshold: 3
        match: { statusCodes: consumer.web.probe.statusCodes }
      }
    }]
    requestRoutingRules: [for consumer in consumers: {
      name: 'route-${consumer.key}'
      properties: {
        ruleType: 'Basic'
        priority: consumer.key == 'primary' ? 100 : 200
        httpListener: { id: resourceId('Microsoft.Network/applicationGateways/httpListeners', gatewayName, 'https-${consumer.key}') }
        backendAddressPool: { id: resourceId('Microsoft.Network/applicationGateways/backendAddressPools', gatewayName, 'backend-${consumer.key}') }
        backendHttpSettings: { id: resourceId('Microsoft.Network/applicationGateways/backendHttpSettingsCollection', gatewayName, 'https-${consumer.key}') }
      }
    }]
  }
  dependsOn: [certificateGrants]
}

output id string = gateway.id
output privateIp string = gateway.properties.frontendIPConfigurations[0].properties.privateIPAddress
output references array = [for (consumer, i) in consumers: {
  consumer: consumer.key
  policyId: policies[i].id
  certificateGrantId: certificateGrants[i].id
  listenerId: '${wafId}/httpListeners/https-${consumer.key}'
  backendId: '${wafId}/backendAddressPools/backend-${consumer.key}'
  probeId: '${wafId}/probes/ready-${consumer.key}'
}]
