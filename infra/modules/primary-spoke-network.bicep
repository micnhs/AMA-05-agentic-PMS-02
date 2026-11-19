@description('Explicit Primary spoke profile, keeping its single-node D4 decision unchanged.')
param profile object
@description('Fresh read-only hub receipt; resources in it are never recreated by this module.')
param receipt object
@description('Enable remote gateway only after the hub-side transit peering exists.')
param useRemoteGateway bool
@description('Consumer ownership only, never the hub ownership marker.')
param tags object

var name = '${profile.namePrefix}-spoke'
var allocations = [
  { role: 'containerApps', name: 'snet-container-apps', prefix: profile.network.containerApps, delegated: true }
  { role: 'workflows', name: 'snet-workflows', prefix: profile.network.workflows, delegated: true }
  { role: 'operations', name: 'snet-operations', prefix: profile.network.operations, delegated: true }
  { role: 'privateEndpoints', name: 'snet-private-endpoints', prefix: profile.network.privateEndpoints, delegated: false }
  { role: 'operator', name: 'snet-operator', prefix: profile.network.operator, delegated: false }
  { role: 'client', name: 'snet-test-client', prefix: profile.network.client, delegated: false }
]
var workloadSources = [profile.network.containerApps, profile.network.workflows, profile.network.operations, profile.network.operator, profile.network.client]

resource routes 'Microsoft.Network/routeTables@2024-07-01' = {
  name: 'rt-${name}-egress'
  location: profile.location
  tags: tags
  properties: {
    disableBgpRoutePropagation: false
    routes: [
      {
        name: 'controlled-default-egress'
        properties: { addressPrefix: '0.0.0.0/0', nextHopType: 'VirtualAppliance', nextHopIpAddress: receipt.hub.firewallPrivateIp }
      }
    ]
  }
}

resource nsgs 'Microsoft.Network/networkSecurityGroups@2024-07-01' = [for subnet in allocations: {
  name: 'nsg-${name}-${subnet.role}'
  location: profile.location
  tags: tags
  properties: {
    securityRules: concat([
      {
        name: 'DenyVpnDirectBackendAndData'
        properties: { priority: 90, direction: 'Inbound', access: 'Deny', protocol: '*', sourceAddressPrefix: receipt.vpn.clientPool, sourcePortRange: '*', destinationAddressPrefix: '*', destinationPortRange: '*' }
      }
      {
        name: 'HubDnsProxyOnly'
        properties: { priority: 100, direction: 'Outbound', access: 'Allow', protocol: '*', sourceAddressPrefix: '*', sourcePortRange: '*', destinationAddressPrefix: receipt.hub.dnsServerIp, destinationPortRange: '53' }
      }
      {
        name: 'DenyPlatformDnsBypass'
        properties: { priority: 110, direction: 'Outbound', access: 'Deny', protocol: '*', sourceAddressPrefix: '*', sourcePortRange: '*', destinationAddressPrefix: 'AzurePlatformDNS', destinationPortRange: '53' }
      }
      {
        name: 'OwnPrivateTlsSql'
        properties: { priority: 120, direction: 'Outbound', access: 'Allow', protocol: 'Tcp', sourceAddressPrefix: '*', sourcePortRange: '*', destinationAddressPrefix: profile.network.vnet, destinationPortRanges: ['443', '1433'] }
      }
      {
        name: 'ReviewedHttpsViaFirewall'
        properties: { priority: 130, direction: 'Outbound', access: 'Allow', protocol: 'Tcp', sourceAddressPrefix: '*', sourcePortRange: '*', destinationAddressPrefix: 'Internet', destinationPortRange: '443' }
      }
    ], subnet.role == 'containerApps' ? [
      {
        name: 'PrivateWafHttpsBackend'
        properties: { priority: 100, direction: 'Inbound', access: 'Allow', protocol: 'Tcp', sourceAddressPrefix: receipt.hub.subnets.waf.addressPrefix, sourcePortRange: '*', destinationAddressPrefix: subnet.prefix, destinationPortRanges: ['443', '31443'] }
      }
      {
        name: 'ManagedEnvironmentInternalInbound'
        properties: { priority: 110, direction: 'Inbound', access: 'Allow', protocol: '*', sourceAddressPrefix: subnet.prefix, sourcePortRange: '*', destinationAddressPrefix: subnet.prefix, destinationPortRange: '*' }
      }
      {
        name: 'RequiredLoadBalancerHealth'
        properties: { priority: 120, direction: 'Inbound', access: 'Allow', protocol: 'Tcp', sourceAddressPrefix: 'AzureLoadBalancer', sourcePortRange: '*', destinationAddressPrefix: subnet.prefix, destinationPortRange: '30000-32767' }
      }
      {
        name: 'OwnBoundWorkloadHttps'
        properties: { priority: 130, direction: 'Inbound', access: 'Allow', protocol: 'Tcp', sourceAddressPrefixes: [profile.network.workflows, profile.network.operations, profile.network.operator], sourcePortRange: '*', destinationAddressPrefix: subnet.prefix, destinationPortRanges: ['443', '31443'] }
      }
      {
        name: 'ManagedEnvironmentInternalOutbound'
        properties: { priority: 140, direction: 'Outbound', access: 'Allow', protocol: '*', sourceAddressPrefix: subnet.prefix, sourcePortRange: '*', destinationAddressPrefix: subnet.prefix, destinationPortRange: '*' }
      }
    ] : [], subnet.role == 'privateEndpoints' ? [
      {
        name: 'OnlyOwnWorkloadsToData'
        properties: { priority: 100, direction: 'Inbound', access: 'Allow', protocol: 'Tcp', sourceAddressPrefixes: workloadSources, sourcePortRange: '*', destinationAddressPrefix: subnet.prefix, destinationPortRanges: ['443', '1433'] }
      }
    ] : [], contains(['operator', 'client'], subnet.role) ? [
      {
        name: 'RequiredVmWireServer'
        properties: { priority: 140, direction: 'Outbound', access: 'Allow', protocol: 'Tcp', sourceAddressPrefix: subnet.prefix, sourcePortRange: '*', destinationAddressPrefix: '168.63.129.16', destinationPortRanges: ['80', '32526'] }
      }
    ] : [], subnet.role == 'client' && profile.access.includeInteractiveClient ? [
      {
        name: 'ReviewedBastionRdpOnly'
        properties: { priority: 100, direction: 'Inbound', access: 'Allow', protocol: 'Tcp', sourceAddressPrefix: profile.network.bastion, sourcePortRange: '*', destinationAddressPrefix: subnet.prefix, destinationPortRange: '3389' }
      }
    ] : [], [
      {
        name: 'DenyOtherInboundIncludingOtherSpokes'
        properties: { priority: 4096, direction: 'Inbound', access: 'Deny', protocol: '*', sourceAddressPrefix: '*', sourcePortRange: '*', destinationAddressPrefix: '*', destinationPortRange: '*' }
      }
      {
        name: 'DenyOtherOutboundIncludingOtherSpokes'
        properties: { priority: 4096, direction: 'Outbound', access: 'Deny', protocol: '*', sourceAddressPrefix: '*', sourcePortRange: '*', destinationAddressPrefix: '*', destinationPortRange: '*' }
      }
    ])
  }
}]
var spokeSubnets = [for (subnet, i) in allocations: {
  name: subnet.name
  properties: union({
    addressPrefix: subnet.prefix
    defaultOutboundAccess: false
    networkSecurityGroup: { id: nsgs[i].id }
    routeTable: { id: routes.id }
  }, subnet.delegated ? {
    delegations: [{ name: 'managed-host', properties: { serviceName: 'Microsoft.App/environments' } }]
  } : {}, subnet.role == 'privateEndpoints' ? { privateEndpointNetworkPolicies: 'Enabled' } : {})
}]
resource vnet 'Microsoft.Network/virtualNetworks@2024-07-01' = {
  name: 'vnet-${name}'
  location: profile.location
  tags: tags
  properties: {
    addressSpace: { addressPrefixes: [profile.network.vnet] }
    dhcpOptions: { dnsServers: [receipt.hub.dnsServerIp] }
    subnets: concat(spokeSubnets, [
      { name: 'AzureBastionSubnet', properties: { addressPrefix: profile.network.bastion, defaultOutboundAccess: false } }
    ])
  }
}
resource peer 'Microsoft.Network/virtualNetworks/virtualNetworkPeerings@2024-07-01' = {
  parent: vnet
  name: 'to-shared-hub'
  properties: {
    remoteVirtualNetwork: { id: receipt.hub.vnetId }
    allowVirtualNetworkAccess: true
    allowForwardedTraffic: true
    allowGatewayTransit: false
    useRemoteGateways: useRemoteGateway
  }
}

var dns = toObject(receipt.dns.zones, zone => zone.name, zone => zone.id)
output network object = {
  id: vnet.id
  endpointsSubnetId: resourceId('Microsoft.Network/virtualNetworks/subnets', vnet.name, 'snet-private-endpoints')
  containerAppsSubnetId: resourceId('Microsoft.Network/virtualNetworks/subnets', vnet.name, 'snet-container-apps')
  workflowsSubnetId: resourceId('Microsoft.Network/virtualNetworks/subnets', vnet.name, 'snet-workflows')
  operationsSubnetId: resourceId('Microsoft.Network/virtualNetworks/subnets', vnet.name, 'snet-operations')
  routeTableId: routes.id
  spokePeeringId: peer.id
  sharedHubGeneration: receipt.generation
  sharedHubReceiptSha256: receipt.receiptSha256
  managePrivateDns: false
  accessSubnets: {
    operator: resourceId('Microsoft.Network/virtualNetworks/subnets', vnet.name, 'snet-operator')
    client: resourceId('Microsoft.Network/virtualNetworks/subnets', vnet.name, 'snet-test-client')
    bastion: resourceId('Microsoft.Network/virtualNetworks/subnets', vnet.name, 'AzureBastionSubnet')
  }
  dns: {
    blob: dns['privatelink.blob.${environment().suffixes.storage}']
    queue: dns['privatelink.queue.${environment().suffixes.storage}']
    table: dns['privatelink.table.${environment().suffixes.storage}']
    sql: dns['privatelink${environment().suffixes.sqlServerHostname}']
    vault: dns['privatelink.vaultcore.azure.net']
    registry: dns['privatelink.azurecr.io']
    search: dns['privatelink.search.windows.net']
    foundry: [dns['privatelink.openai.azure.com'], dns['privatelink.cognitiveservices.azure.com'], dns['privatelink.services.ai.azure.com']]
    functions: dns['privatelink.azurewebsites.net']
    monitor: [dns['privatelink.monitor.azure.com'], dns['privatelink.oms.opinsights.azure.com'], dns['privatelink.ods.opinsights.azure.com'], dns['privatelink.agentsvc.azure-automation.net'], dns['privatelink.blob.${environment().suffixes.storage}']]
  }
}
