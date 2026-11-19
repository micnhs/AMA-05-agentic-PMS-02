@description('Short workload name with deterministic suffix.')
param name string

@description('Approved EU candidate region.')
param location string

@description('Dedicated Container Apps workload-profiles subnet resource ID.')
param subnetId string

@description('VNet resource ID for private ingress DNS.')
param vnetId string

@description('Log Analytics workspace resource ID for platform logs.')
param workspaceId string

@description('Enable only after zonal support, replica strategy and cost review.')
param zoneRedundant bool

@description('Explicit hosting candidate. D4 is not selected implicitly by choosing a workload-profiles environment.')
@allowed(['Consumption', 'D4'])
param workloadProfileType string = 'Consumption'
@description('False only for a shared-hub consumer; its actual default domain/static IP is registered by the hub owner.')
param managePrivateDns bool = true

@description('Non-sensitive cost and ownership tags.')
param tags object

resource environment 'Microsoft.App/managedEnvironments@2025-07-01' = {
  name: 'cae-${name}'
  location: location
  tags: tags
  properties: {
    publicNetworkAccess: 'Disabled'
    zoneRedundant: zoneRedundant
    vnetConfiguration: {
      internal: true
      infrastructureSubnetId: subnetId
    }
    workloadProfiles: concat([
      {
        name: 'Consumption'
        workloadProfileType: 'Consumption'
      }
    ], workloadProfileType == 'D4' ? [
      {
        name: 'D4'
        workloadProfileType: 'D4'
        minimumCount: 1
        maximumCount: 1
      }
    ] : [])
    appLogsConfiguration: {
      destination: 'azure-monitor'
    }
    peerTrafficConfiguration: {
      encryption: { enabled: true }
    }
  }
}

resource logs 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'platform-only'
  scope: environment
  properties: {
    workspaceId: workspaceId
    logs: [
      { category: 'ContainerAppSystemLogs', enabled: true }
    ]
    metrics: [
      { category: 'AllMetrics', enabled: true }
    ]
  }
}

module ingressDns 'private-ingress-dns.bicep' = if (managePrivateDns) {
  name: 'ingress-dns-${name}'
  params: {
    name: name
    domain: environment.properties.defaultDomain
    privateIp: environment.properties.staticIp
    vnetId: vnetId
    tags: tags
  }
}

output id string = environment.id
output defaultDomain string = environment.properties.defaultDomain
output staticIp string = environment.properties.staticIp
output workloadProfileName 'Consumption' | 'D4' = workloadProfileType
