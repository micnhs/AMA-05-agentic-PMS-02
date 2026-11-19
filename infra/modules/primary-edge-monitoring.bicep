@description('Actual own Firewall name from the completed network stage.')
param firewallName string
@description('Actual own VPN Gateway name from the completed network stage.')
param gatewayName string
@description('Actual private workload workspace created by the foundation stage.')
param workspaceId string

resource firewall 'Microsoft.Network/azureFirewalls@2024-07-01' existing = {
  name: firewallName
}

resource gateway 'Microsoft.Network/virtualNetworkGateways@2024-07-01' existing = {
  name: gatewayName
}

resource firewallMetrics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'metrics-only'
  scope: firewall
  properties: {
    workspaceId: workspaceId
    logs: []
    metrics: [{ category: 'AllMetrics', enabled: true }]
  }
}

resource gatewayMetrics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'metrics-only'
  scope: gateway
  properties: {
    workspaceId: workspaceId
    logs: []
    metrics: [{ category: 'AllMetrics', enabled: true }]
  }
}

output resources object = {
  firewall: firewallMetrics.id
  gateway: gatewayMetrics.id
}
