@description('Validated independent hub profile. CIDRs, zones and WAF size are explicit private inputs.')
param profile object
@description('Enable P2S and custom DNS only after actual owned Firewall readback.')
param bound bool
@description('Preceding actual Firewall private IP; no inferred .4 address.')
param firewallPrivateIp string
@description('Hub-owned single-tenant audience, not either consumer application.')
param vpnAudienceClientId string
@description('Normalized DNS/HTTPS policy from the bound profile.')
param edgePolicy object
@description('Hub ownership and unchanged original expiry.')
param tags object

var name = '${profile.namePrefix}-hub'

resource wafNsg 'Microsoft.Network/networkSecurityGroups@2024-07-01' = {
  name: 'nsg-${name}-waf'
  location: profile.location
  tags: tags
  properties: {
    securityRules: [
      {
        name: 'PrivateVpnHttpsOnly'
        properties: { priority: 100, direction: 'Inbound', access: 'Allow', protocol: 'Tcp', sourceAddressPrefix: profile.network.vpnClientPool, sourcePortRange: '*', destinationAddressPrefix: profile.waf.frontendPrivateIp, destinationPortRange: '443' }
      }
      {
        name: 'RequiredAzureLoadBalancerHealth'
        properties: { priority: 110, direction: 'Inbound', access: 'Allow', protocol: '*', sourceAddressPrefix: 'AzureLoadBalancer', sourcePortRange: '*', destinationAddressPrefix: '*', destinationPortRange: '*' }
      }
      {
        name: 'DenyOtherInbound'
        properties: { priority: 4096, direction: 'Inbound', access: 'Deny', protocol: '*', sourceAddressPrefix: '*', sourcePortRange: '*', destinationAddressPrefix: '*', destinationPortRange: '*' }
      }
      {
        name: 'DenyUnboundOutbound'
        properties: { priority: 4096, direction: 'Outbound', access: 'Deny', protocol: '*', sourceAddressPrefix: '*', sourcePortRange: '*', destinationAddressPrefix: '*', destinationPortRange: '*' }
      }
    ]
  }
}

resource endpointNsg 'Microsoft.Network/networkSecurityGroups@2024-07-01' = {
  name: 'nsg-${name}-endpoints'
  location: profile.location
  tags: tags
  properties: {
    securityRules: [
      {
        name: 'WafPrivateVaultOnly'
        properties: { priority: 100, direction: 'Inbound', access: 'Allow', protocol: 'Tcp', sourceAddressPrefix: profile.network.waf, sourcePortRange: '*', destinationAddressPrefix: profile.network.endpoints, destinationPortRange: '443' }
      }
      {
        name: 'DenyOtherInbound'
        properties: { priority: 4096, direction: 'Inbound', access: 'Deny', protocol: '*', sourceAddressPrefix: '*', sourcePortRange: '*', destinationAddressPrefix: '*', destinationPortRange: '*' }
      }
    ]
  }
}

resource vnet 'Microsoft.Network/virtualNetworks@2024-07-01' = {
  name: 'vnet-${name}'
  location: profile.location
  tags: tags
  properties: {
    addressSpace: { addressPrefixes: [profile.network.vnet] }
    dhcpOptions: bound ? { dnsServers: [firewallPrivateIp] } : {}
    subnets: [
      { name: 'AzureFirewallSubnet', properties: { addressPrefix: profile.network.firewall, defaultOutboundAccess: false } }
      { name: 'GatewaySubnet', properties: { addressPrefix: profile.network.gateway, defaultOutboundAccess: false } }
      {
        name: 'snet-waf'
        properties: {
          addressPrefix: profile.network.waf
          defaultOutboundAccess: false
          networkSecurityGroup: { id: wafNsg.id }
          delegations: [{ name: 'isolated-application-gateway', properties: { serviceName: 'Microsoft.Network/applicationGateways' } }]
        }
      }
      {
        name: 'snet-hub-endpoints'
        properties: {
          addressPrefix: profile.network.endpoints
          defaultOutboundAccess: false
          privateEndpointNetworkPolicies: 'Enabled'
          networkSecurityGroup: { id: endpointNsg.id }
        }
      }
    ]
  }
}

module policy 'primary-edge-policy.bicep' = {
  name: '${name}-policy'
  params: {
    name: name
    location: profile.location
    virtualNetworkId: vnet.id
    bindDnsPolicy: bound
    dnsLinkName: 'shared-hub'
    denyTransitivePrivateTraffic: true
    dnsDomains: edgePolicy.dnsDomains
    httpsRules: edgePolicy.httpsRules
    privateOnlyFqdns: edgePolicy.privateOnlyFqdns
    sourcePrefixes: edgePolicy.sourcePrefixes
    tags: tags
  }
}

module edge 'primary-edge.bicep' = {
  name: '${name}-edge'
  params: {
    name: name
    location: profile.location
    subnets: {
      firewall: resourceId('Microsoft.Network/virtualNetworks/subnets', vnet.name, 'AzureFirewallSubnet')
      gateway: resourceId('Microsoft.Network/virtualNetworks/subnets', vnet.name, 'GatewaySubnet')
    }
    firewallPolicyId: policy.outputs.firewallPolicyId
    zones: profile.network.zones
    vpnClientPrefix: profile.network.vpnClientPool
    enablePointToSite: bound
    tenantId: profile.binding.tenantId
    vpnAudienceClientId: vpnAudienceClientId
    tags: tags
  }
}

resource zones 'Microsoft.Network/privateDnsZones@2020-06-01' = [for zone in profile.dnsZones: {
  name: zone
  location: 'global'
  tags: tags
}]

resource links 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = [for (zone, i) in profile.dnsZones: {
  parent: zones[i]
  name: 'hub'
  location: 'global'
  tags: tags
  properties: { registrationEnabled: false, virtualNetwork: { id: vnet.id } }
}]

resource wafIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: 'id-${name}-waf'
  location: profile.location
  tags: tags
}

resource certificateVault 'Microsoft.KeyVault/vaults@2024-11-01' = {
  name: 'kv-${profile.namePrefix}-${take(uniqueString(resourceGroup().id), 8)}'
  location: profile.location
  tags: tags
  properties: {
    tenantId: profile.binding.tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
    enablePurgeProtection: true
    publicNetworkAccess: 'Disabled'
    enabledForDeployment: false
    enabledForDiskEncryption: false
    enabledForTemplateDeployment: false
    networkAcls: { bypass: 'None', defaultAction: 'Deny', ipRules: [], virtualNetworkRules: [] }
  }
}

module vaultEndpoint 'private-endpoint.bicep' = {
  name: '${name}-vault-endpoint'
  params: {
    name: 'pe-${name}-vault'
    location: profile.location
    subnetId: resourceId('Microsoft.Network/virtualNetworks/subnets', vnet.name, 'snet-hub-endpoints')
    targetResourceId: certificateVault.id
    groupId: 'vault'
    privateDnsZoneIds: [resourceId('Microsoft.Network/privateDnsZones', 'privatelink.vaultcore.azure.net')]
    tags: tags
  }
  dependsOn: [zones]
}

var zoneReferences = [for (zone, i) in profile.dnsZones: { name: zone, id: zones[i].id }]
output hub object = {
  resourceGroupId: resourceGroup().id
  vnetId: vnet.id
  location: profile.location
  addressPrefix: profile.network.vnet
  subnets: {
    firewall: { id: resourceId('Microsoft.Network/virtualNetworks/subnets', vnet.name, 'AzureFirewallSubnet'), addressPrefix: profile.network.firewall }
    gateway: { id: resourceId('Microsoft.Network/virtualNetworks/subnets', vnet.name, 'GatewaySubnet'), addressPrefix: profile.network.gateway }
    waf: { id: resourceId('Microsoft.Network/virtualNetworks/subnets', vnet.name, 'snet-waf'), addressPrefix: profile.network.waf }
    endpoints: { id: resourceId('Microsoft.Network/virtualNetworks/subnets', vnet.name, 'snet-hub-endpoints'), addressPrefix: profile.network.endpoints }
  }
  firewallId: edge.outputs.resources.firewallId
  firewallPolicyId: edge.outputs.resources.firewallPolicyId
  firewallPrivateIp: edge.outputs.firewallPrivateIp
  vpnGatewayId: edge.outputs.resources.gatewayId
  dnsPolicyId: policy.outputs.dnsPolicyId
  dnsServerIp: edge.outputs.firewallPrivateIp
  wafId: null
  wafPrivateIp: null
  ownerDependencies: {
    wafIdentityId: wafIdentity.id
    wafIdentityPrincipalId: wafIdentity.properties.principalId
    certificateVaultId: certificateVault.id
    certificateVaultUri: certificateVault.properties.vaultUri
    certificateEndpointId: resourceId('Microsoft.Network/privateEndpoints', 'pe-${name}-vault')
    zoneIds: zoneReferences
  }
}
output edge object = {
  resources: union(edge.outputs.resources, policy.outputs.resources)
  firewallPrivateIp: edge.outputs.firewallPrivateIp
  dnsPolicyId: policy.outputs.dnsPolicyId
  dnsPolicyLinked: policy.outputs.dnsPolicyLinked
  clientAccess: edge.outputs.clientAccess
}
