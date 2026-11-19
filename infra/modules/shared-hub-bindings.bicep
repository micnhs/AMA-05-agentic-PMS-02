@description('Independent hub owner profile.')
param profile object
@description('Actual owned hub resource outputs, freshly read back before changing shared state.')
param hub object
@description('Exact VNet/vault/secret names, source-bound to the preceding readback and reviewed HTTPS bindings.')
param resourceNames object
@description('Complete validated monotonic snapshot preserving the other consumer exactly.')
param bindings object
@description('Complete source-scoped egress/DNS policy for retained consumers.')
param edgePolicy object
@description('Hub ownership and original expiry.')
param tags object

var name = '${profile.namePrefix}-hub'
var active = map(filter(items(bindings.consumers), item => item.value != null && item.value.state != 'reserved' && item.value.state != 'offboarded'), item => union(item.value, { key: item.key }))
var web = filter(active, item => item.state == 'web-bound')
var records = flatten(map(active, consumer => map(consumer.dnsRecords, record => union(record, { consumer: consumer.key, bindingHash: consumer.bindingHash }))))
var webZones = map(web, item => item.web.frontendZone)
var allZones = union(profile.dnsZones, map(records, record => record.zone), webZones)
var links = flatten(map(active, consumer => map(union(profile.dnsZones, map(consumer.dnsRecords, record => record.zone), consumer.web == null ? [] : [consumer.web.frontendZone]), zone => { consumer: consumer.key, vnetId: consumer.spoke.vnetId, zone: zone })))

resource vnet 'Microsoft.Network/virtualNetworks@2024-07-01' existing = {
  name: resourceNames.vnet
}
resource peers 'Microsoft.Network/virtualNetworks/virtualNetworkPeerings@2024-07-01' = [for consumer in active: {
  parent: vnet
  name: 'to-${consumer.key}'
  properties: {
    remoteVirtualNetwork: { id: consumer.spoke.vnetId }
    allowVirtualNetworkAccess: true
    allowForwardedTraffic: false
    allowGatewayTransit: true
    useRemoteGateways: false
  }
}]

module policy 'primary-edge-policy.bicep' = {
  name: '${name}-consumer-policy'
  params: {
    name: name
    location: profile.location
    virtualNetworkId: hub.vnetId
    bindDnsPolicy: true
    dnsLinkName: 'shared-hub'
    denyTransitivePrivateTraffic: true
    dnsDomains: union(edgePolicy.dnsDomains, allZones)
    httpsRules: edgePolicy.httpsRules
    privateOnlyFqdns: edgePolicy.privateOnlyFqdns
    sourcePrefixes: edgePolicy.sourcePrefixes
    tags: tags
  }
}

resource zones 'Microsoft.Network/privateDnsZones@2020-06-01' = [for zone in allZones: {
  name: zone
  location: 'global'
  tags: tags
}]
resource hubLinks 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = [for (zone, i) in allZones: {
  parent: zones[i]
  name: 'hub'
  location: 'global'
  tags: tags
  properties: { registrationEnabled: false, virtualNetwork: { id: hub.vnetId } }
}]
resource spokeLinks 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = [for link in links: {
  name: '${link.zone}/consumer-${link.consumer}'
  location: 'global'
  tags: tags
  properties: { registrationEnabled: false, virtualNetwork: { id: link.vnetId } }
  dependsOn: [zones]
}]
resource dataRecords 'Microsoft.Network/privateDnsZones/A@2020-06-01' = [for record in records: {
  name: '${record.zone}/${record.name}'
  properties: {
    ttl: 300
    metadata: { cohorsBinding: profile.bindingHash, cohorsConsumer: record.bindingHash }
    aRecords: [for address in record.addresses: { ipv4Address: address }]
  }
  dependsOn: [zones]
}]
resource frontendRecords 'Microsoft.Network/privateDnsZones/A@2020-06-01' = [for consumer in web: {
  name: '${consumer.web.frontendZone}/${consumer.web.frontendRecord}'
  properties: {
    ttl: 300
    metadata: { cohorsBinding: profile.bindingHash, cohorsConsumer: consumer.bindingHash }
    aRecords: [{ ipv4Address: profile.waf.frontendPrivateIp }]
  }
  dependsOn: [zones]
}]

resource wafRoutes 'Microsoft.Network/routeTables@2024-07-01' = {
  name: 'rt-${name}-waf'
  location: profile.location
  tags: tags
  properties: {
    disableBgpRoutePropagation: false
    routes: [
      {
        name: 'reviewed-public-egress-only'
        properties: { addressPrefix: '0.0.0.0/0', nextHopType: 'VirtualAppliance', nextHopIpAddress: hub.firewallPrivateIp }
      }
    ]
  }
}
resource wafNsg 'Microsoft.Network/networkSecurityGroups@2024-07-01' = {
  name: 'nsg-${name}-waf'
  location: profile.location
  tags: tags
  properties: {
    securityRules: concat(empty(web) ? [] : [
      {
        name: 'PrivateVpnHttpsOnly'
        properties: { priority: 100, direction: 'Inbound', access: 'Allow', protocol: 'Tcp', sourceAddressPrefix: profile.network.vpnClientPool, sourcePortRange: '*', destinationAddressPrefix: profile.waf.frontendPrivateIp, destinationPortRange: '443' }
      }
      {
        name: 'PrivateBackendHttps'
        properties: { priority: 100, direction: 'Outbound', access: 'Allow', protocol: 'Tcp', sourceAddressPrefix: profile.network.waf, sourcePortRange: '*', destinationAddressPrefixes: concat(map(web, item => item.web.backendPrivateIp), hub.ownerDependencies.certificateEndpointIps), destinationPortRange: '443' }
      }
    ], [
      {
        name: 'RequiredAzureLoadBalancerHealth'
        properties: { priority: 110, direction: 'Inbound', access: 'Allow', protocol: '*', sourceAddressPrefix: 'AzureLoadBalancer', sourcePortRange: '*', destinationAddressPrefix: '*', destinationPortRange: '*' }
      }
      {
        name: 'HubDnsProxyOnly'
        properties: { priority: 110, direction: 'Outbound', access: 'Allow', protocol: '*', sourceAddressPrefix: '*', sourcePortRange: '*', destinationAddressPrefix: hub.firewallPrivateIp, destinationPortRange: '53' }
      }
      {
        name: 'ReviewedHttpsViaFirewall'
        properties: { priority: 120, direction: 'Outbound', access: 'Allow', protocol: 'Tcp', sourceAddressPrefix: '*', sourcePortRange: '*', destinationAddressPrefix: 'Internet', destinationPortRange: '443' }
      }
      {
        name: 'DenyPlatformDnsBypass'
        properties: { priority: 130, direction: 'Outbound', access: 'Deny', protocol: '*', sourceAddressPrefix: '*', sourcePortRange: '*', destinationAddressPrefix: 'AzurePlatformDNS', destinationPortRange: '53' }
      }
      {
        name: 'DenyOtherInbound'
        properties: { priority: 4096, direction: 'Inbound', access: 'Deny', protocol: '*', sourceAddressPrefix: '*', sourcePortRange: '*', destinationAddressPrefix: '*', destinationPortRange: '*' }
      }
      {
        name: 'DenyOtherOutbound'
        properties: { priority: 4096, direction: 'Outbound', access: 'Deny', protocol: '*', sourceAddressPrefix: '*', sourcePortRange: '*', destinationAddressPrefix: '*', destinationPortRange: '*' }
      }
    ])
  }
}
resource wafSubnet 'Microsoft.Network/virtualNetworks/subnets@2024-07-01' = {
  parent: vnet
  name: 'snet-waf'
  properties: {
    addressPrefix: profile.network.waf
    defaultOutboundAccess: false
    networkSecurityGroup: { id: wafNsg.id }
    routeTable: { id: wafRoutes.id }
    delegations: [{ name: 'isolated-application-gateway', properties: { serviceName: 'Microsoft.Network/applicationGateways' } }]
  }
}

module waf 'shared-hub-waf.bicep' = if (!empty(web)) {
  name: '${name}-waf'
  params: {
    name: name
    location: profile.location
    subnetId: wafSubnet.id
    privateIp: profile.waf.frontendPrivateIp
    instanceCount: profile.waf.instanceCount
    dependencies: hub.ownerDependencies
    resourceNames: resourceNames
    consumers: web
    vpnClientPrefix: profile.network.vpnClientPool
    tags: tags
  }
  dependsOn: [peers, dataRecords, hubLinks, policy]
}

output wafId string? = empty(web) ? null : waf!.outputs.id
output wafPrivateIp string? = empty(web) ? null : waf!.outputs.privateIp
var peerReferences = [for (consumer, i) in active: { consumer: consumer.key, id: peers[i].id }]
var linkReferences = [for link in links: { consumer: link.consumer, id: resourceId('Microsoft.Network/privateDnsZones/virtualNetworkLinks', link.zone, 'consumer-${link.consumer}') }]
var recordReferences = [for record in records: { consumer: record.consumer, id: resourceId('Microsoft.Network/privateDnsZones/A', record.zone, record.name) }]
var frontendReferences = [for consumer in web: { consumer: consumer.key, id: resourceId('Microsoft.Network/privateDnsZones/A', consumer.web.frontendZone, consumer.web.frontendRecord) }]
output references object = {
  peers: peerReferences
  dnsLinks: linkReferences
  dataRecords: recordReferences
  frontendRecords: frontendReferences
  web: empty(web) ? [] : waf!.outputs.references
}
