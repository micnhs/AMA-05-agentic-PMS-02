@description('Owned primary network name.')
param name string
param location string
@description('Own AzureFirewallSubnet and GatewaySubnet from this deployment.')
param subnets object
param firewallPolicyId string
@description('Explicit regional zone candidate; live capability/capacity is a separate gate.')
param zones string[]
@description('Reviewed address pool, never overlapping the own VNet or reviewed client networks.')
param vpnClientPrefix string
@description('Enable only in the separately approved IP/DNS/route binding stage.')
param enablePointToSite bool
@description('Bound original tenant; no authentication-context switch.')
param tenantId string
@description('Actual own assignment-required VPN audience application. Never the tenant-wide Microsoft client audience.')
param vpnAudienceClientId string
param tags object

resource firewallIp 'Microsoft.Network/publicIPAddresses@2024-07-01' = {
  name: 'pip-${name}-firewall'
  location: location
  tags: tags
  zones: zones
  sku: { name: 'Standard', tier: 'Regional' }
  properties: {
    publicIPAllocationMethod: 'Static'
    publicIPAddressVersion: 'IPv4'
  }
}

resource firewall 'Microsoft.Network/azureFirewalls@2024-07-01' = {
  name: 'afw-${name}'
  location: location
  tags: tags
  zones: zones
  properties: {
    sku: { name: 'AZFW_VNet', tier: 'Standard' }
    firewallPolicy: { id: firewallPolicyId }
    threatIntelMode: 'Deny'
    ipConfigurations: [
      {
        name: 'own-egress'
        properties: {
          subnet: { id: subnets.firewall }
          publicIPAddress: { id: firewallIp.id }
        }
      }
    ]
  }
}

resource gatewayIp 'Microsoft.Network/publicIPAddresses@2024-07-01' = {
  name: 'pip-${name}-vpn'
  location: location
  tags: tags
  zones: zones
  sku: { name: 'Standard', tier: 'Regional' }
  properties: {
    publicIPAllocationMethod: 'Static'
    publicIPAddressVersion: 'IPv4'
  }
}

resource gateway 'Microsoft.Network/virtualNetworkGateways@2024-07-01' = {
  name: 'vgw-${name}'
  location: location
  tags: tags
  properties: union({
    gatewayType: 'Vpn'
    vpnType: 'RouteBased'
    vpnGatewayGeneration: 'Generation1'
    sku: { name: 'VpnGw1AZ', tier: 'VpnGw1AZ' }
    enableBgp: false
    activeActive: false
    enablePrivateIpAddress: false
    ipConfigurations: [
      {
        name: 'gateway'
        properties: {
          privateIPAllocationMethod: 'Dynamic'
          subnet: { id: subnets.gateway }
          publicIPAddress: { id: gatewayIp.id }
        }
      }
    ]
  }, enablePointToSite ? {
    vpnClientConfiguration: {
      vpnClientAddressPool: { addressPrefixes: [vpnClientPrefix] }
      vpnClientProtocols: ['OpenVPN']
      vpnAuthenticationTypes: ['AAD']
      aadTenant: uri(environment().authentication.loginEndpoint, tenantId)
      aadIssuer: 'https://sts.windows.net/${tenantId}/'
      aadAudience: vpnAudienceClientId
    }
  } : {})
}

output resources object = {
  firewallId: firewall.id
  firewallPolicyId: firewallPolicyId
  firewallPublicIpId: firewallIp.id
  gatewayId: gateway.id
  gatewayPublicIpId: gatewayIp.id
}
output firewallPrivateIp string = firewall.properties.ipConfigurations[0].properties.privateIPAddress
output clientAccess object = {
  pointToSiteConfigurationRequested: enablePointToSite
  reservedClientPool: vpnClientPrefix
  profileGenerated: false
  clientInstalledOrConnected: false
}
