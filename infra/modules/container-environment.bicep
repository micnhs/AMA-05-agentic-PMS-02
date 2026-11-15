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
    workloadProfiles: [
      { name: 'Consumption', workloadProfileType: 'Consumption' }
    ]
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

module ingressDns 'private-ingress-dns.bicep' = {
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
