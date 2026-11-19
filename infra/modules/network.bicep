@description('Short lowercase workload name plus deterministic suffix.')
param name string

@description('Approved EU candidate region. Availability is not inferred from this value.')
param location string

@description('Approved, nonoverlapping IPv4 VNet range.')
param addressPrefix string

@description('Dedicated Container Apps workload-profiles subnet; example /23.')
param containerAppsPrefix string

@description('Dedicated Flex Workflow subnet; example /26.')
param workflowsPrefix string

@description('Separate Flex Operations subnet; example /26.')
param operationsPrefix string

@description('Nondelegated private endpoint subnet; example /24.')
param endpointsPrefix string

@description('Existing reviewed route table ID with controlled egress to a reachable firewall/NVA. No hub, peering or routes are invented here.')
param egressRouteTableResourceId string = ''

@description('Create an isolated route table and operator/NVA/client/Bastion subnets. No external hub or NAT-only fallback.')
param createOwnedAccess bool = false

@description('Dedicated own NVA address. Must be the fourth usable IPv4 address in egressPrefix.')
param nvaPrivateIp string = '10.42.4.4'
param egressPrefix string = '10.42.4.0/27'
param operatorPrefix string = '10.42.4.32/27'
param clientPrefix string = '10.42.4.64/27'
param bastionPrefix string = '10.42.5.0/26'

@description('Selected primary managed edge. Exclusive with the legacy NVA subnet and route.')
param managedEdge bool = false
@description('Azure Firewall dedicated subnet, at least /26. Candidate only until address review.')
param firewallPrefix string = '10.42.6.0/26'
@description('VPN Gateway dedicated subnet, at least /27. No NSG and no default route.')
param gatewayPrefix string = '10.42.6.64/27'
@description('Separately reviewed nonoverlapping P2S client pool; not another VNet subnet.')
param vpnClientPrefix string = '172.28.240.0/24'
@description('Actual Firewall private IP from the preceding owned ARM/readback stage. Empty keeps routes blackholed and client DNS blocked.')
param firewallPrivateIp string = ''
@description('Caller-derived DNS activation: owned NVA access or the actually bound managed Firewall phase.')
param configureCustomDns bool
@description('Caller-derived exact NVA/Firewall proxy address; never a public DNS fallback.')
param dnsProxyAddress string

@description('Non-sensitive cost and ownership tags.')
param tags object

var workloadSubnets = [
  { name: 'snet-container-apps', prefix: containerAppsPrefix }
  { name: 'snet-workflows', prefix: workflowsPrefix }
  { name: 'snet-operations', prefix: operationsPrefix }
]

var managedEdgeBound = managedEdge && !empty(firewallPrivateIp)
var filterAddress = managedEdge ? firewallPrivateIp : nvaPrivateIp

resource ownedRoutes 'Microsoft.Network/routeTables@2024-07-01' = if (createOwnedAccess) {
  name: 'rt-${name}-filtered'
  location: location
  tags: tags
  properties: {
    disableBgpRoutePropagation: true
    routes: concat([
      {
        name: 'default-controlled-egress'
        properties: union({
          addressPrefix: '0.0.0.0/0'
          nextHopType: managedEdge && !managedEdgeBound ? 'None' : 'VirtualAppliance'
        }, managedEdge && !managedEdgeBound ? {} : { nextHopIpAddress: filterAddress })
      }
    ], managedEdgeBound ? [
      {
        name: 'vpn-return-via-firewall'
        properties: {
          addressPrefix: vpnClientPrefix
          nextHopType: 'VirtualAppliance'
          nextHopIpAddress: firewallPrivateIp
        }
      }
    ] : [])
  }
}

var workloadPrefixes = [for subnet in workloadSubnets: subnet.prefix]
var vpnDestinations = concat(workloadPrefixes, [
  endpointsPrefix
  operatorPrefix
  clientPrefix
  bastionPrefix
])

resource gatewayRoutes 'Microsoft.Network/routeTables@2024-07-01' = if (managedEdge) {
  name: 'rt-${name}-vpn'
  location: location
  tags: tags
  properties: {
    disableBgpRoutePropagation: false
    routes: [for (prefix, i) in vpnDestinations: {
      name: 'private-target-${i}'
      properties: union({
        addressPrefix: prefix
        nextHopType: managedEdgeBound ? 'VirtualAppliance' : 'None'
      }, managedEdgeBound ? { nextHopIpAddress: firewallPrivateIp } : {})
    }]
  }
}

var routeTableId = createOwnedAccess ? ownedRoutes.id : egressRouteTableResourceId
var filteredOutbound = concat(!managedEdge || managedEdgeBound ? [
  {
    name: 'AllowOwnDns'
    properties: {
      priority: 100
      direction: 'Outbound'
      access: 'Allow'
      protocol: '*'
      sourceAddressPrefix: '*'
      sourcePortRange: '*'
      destinationAddressPrefix: filterAddress
      destinationPortRange: '53'
    }
  }
 ] : [], [
  {
    name: 'DenyPlatformDnsBypass'
    properties: {
      priority: 110
      direction: 'Outbound'
      access: 'Deny'
      protocol: '*'
      sourceAddressPrefix: '*'
      sourcePortRange: '*'
      destinationAddressPrefix: 'AzurePlatformDNS'
      destinationPortRange: '53'
    }
  }
  {
    name: 'DenyOtherDns'
    properties: {
      priority: 120
      direction: 'Outbound'
      access: 'Deny'
      protocol: '*'
      sourceAddressPrefix: '*'
      sourcePortRange: '*'
      destinationAddressPrefix: '*'
      destinationPortRanges: ['53', '853']
    }
  }
  {
    name: 'AllowPrivateTlsSql'
    properties: {
      priority: 130
      direction: 'Outbound'
      access: 'Allow'
      protocol: 'Tcp'
      sourceAddressPrefix: '*'
      sourcePortRange: '*'
      destinationAddressPrefix: addressPrefix
      destinationPortRanges: ['443', '1433']
    }
  }
  {
    name: 'AllowTlsSubjectToControlledRoute'
    properties: {
      priority: 140
      direction: 'Outbound'
      access: 'Allow'
      protocol: 'Tcp'
      sourceAddressPrefix: '*'
      sourcePortRange: '*'
      destinationAddressPrefix: 'Internet'
      destinationPortRange: '443'
    }
  }
  {
    name: 'DenyOtherInternetIncludingQuic'
    properties: {
      priority: 150
      direction: 'Outbound'
      access: 'Deny'
      protocol: '*'
      sourceAddressPrefix: '*'
      sourcePortRange: '*'
      destinationAddressPrefix: 'Internet'
      destinationPortRange: '*'
    }
  }
  {
    name: 'DenyIpv6'
    properties: {
      priority: 90
      direction: 'Outbound'
      access: 'Deny'
      protocol: '*'
      sourceAddressPrefix: '*'
      sourcePortRange: '*'
      destinationAddressPrefix: '::/0'
      destinationPortRange: '*'
    }
  }
])

var denyVpnClients = managedEdge ? [
  {
    name: 'DenyOtherVpnClientIngress'
    properties: {
      priority: 200
      direction: 'Inbound'
      access: 'Deny'
      protocol: '*'
      sourceAddressPrefix: vpnClientPrefix
      sourcePortRange: '*'
      destinationAddressPrefix: '*'
      destinationPortRange: '*'
    }
  }
] : []

resource workloadNsgs 'Microsoft.Network/networkSecurityGroups@2024-07-01' = [for subnet in workloadSubnets: {
  name: 'nsg-${name}-${replace(subnet.name, 'snet-', '')}'
  location: location
  tags: tags
  properties: {
    securityRules: createOwnedAccess ? concat(filteredOutbound, managedEdgeBound && subnet.name == 'snet-container-apps' ? [
      {
        name: 'AllowFirewallPrivateHttpsProxy'
        properties: {
          priority: 100
          direction: 'Inbound'
          access: 'Allow'
          protocol: 'Tcp'
          sourceAddressPrefix: firewallPrivateIp
          sourcePortRange: '*'
          destinationAddressPrefix: containerAppsPrefix
          destinationPortRanges: ['443', '31443']
        }
      }
    ] : [], denyVpnClients) : []
  }
}]

resource endpointNsg 'Microsoft.Network/networkSecurityGroups@2024-07-01' = {
  name: 'nsg-${name}-endpoints'
  location: location
  tags: tags
  properties: {
    securityRules: concat(managedEdge ? [
      {
        name: 'DenyVpnDirectDataAccess'
        properties: {
          priority: 90
          direction: 'Inbound'
          access: 'Deny'
          protocol: '*'
          sourceAddressPrefix: vpnClientPrefix
          sourcePortRange: '*'
          destinationAddressPrefix: '*'
          destinationPortRange: '*'
        }
      }
    ] : [], [
      {
        name: 'AllowPrivateTlsAndSqlProxy'
        properties: {
          priority: 100
          direction: 'Inbound'
          access: 'Allow'
          protocol: 'Tcp'
          sourceAddressPrefix: 'VirtualNetwork'
          sourcePortRange: '*'
          destinationAddressPrefix: '*'
          destinationPortRanges: ['443', '1433']
        }
      }
      {
        name: 'DenyOtherInbound'
        properties: {
          priority: 4096
          direction: 'Inbound'
          access: 'Deny'
          protocol: '*'
          sourceAddressPrefix: '*'
          sourcePortRange: '*'
          destinationAddressPrefix: '*'
          destinationPortRange: '*'
        }
      }
    ])
  }
}

var integratedSubnets = [for (subnet, i) in workloadSubnets: {
  name: subnet.name
  properties: {
    addressPrefix: subnet.prefix
    defaultOutboundAccess: false
    delegations: [
      {
        name: 'managed-host'
        properties: {
          serviceName: 'Microsoft.App/environments'
        }
      }
    ]
    networkSecurityGroup: {
      id: workloadNsgs[i].id
    }
    routeTable: {
      id: routeTableId
    }
  }
}]

resource accessNsg 'Microsoft.Network/networkSecurityGroups@2024-07-01' = if (createOwnedAccess) {
  name: 'nsg-${name}-operator'
  location: location
  tags: tags
  properties: {
    securityRules: concat(filteredOutbound, [
      {
        name: 'AllowBastionRdpOnly'
        properties: {
          priority: 100
          direction: 'Inbound'
          access: 'Allow'
          protocol: 'Tcp'
          sourceAddressPrefix: bastionPrefix
          sourcePortRange: '*'
          destinationAddressPrefix: clientPrefix
          destinationPortRange: '3389'
        }
      }
      {
        name: 'DenyOtherInbound'
        properties: {
          priority: 4096
          direction: 'Inbound'
          access: 'Deny'
          protocol: '*'
          sourceAddressPrefix: '*'
          sourcePortRange: '*'
          destinationAddressPrefix: '*'
          destinationPortRange: '*'
        }
      }
    ])
  }
}

resource nvaNsg 'Microsoft.Network/networkSecurityGroups@2024-07-01' = if (createOwnedAccess && !managedEdge) {
  name: 'nsg-${name}-filter'
  location: location
  tags: tags
  properties: {
    securityRules: [
      {
        name: 'AllowInspectedTlsAndDnsFromOwnNetwork'
        properties: {
          priority: 100
          direction: 'Inbound'
          access: 'Allow'
          protocol: '*'
          sourceAddressPrefix: addressPrefix
          sourcePortRange: '*'
          destinationAddressPrefix: '*'
          destinationPortRanges: ['443', '53']
        }
      }
      {
        name: 'DenyOtherInbound'
        properties: {
          priority: 4096
          direction: 'Inbound'
          access: 'Deny'
          protocol: '*'
          sourceAddressPrefix: '*'
          sourcePortRange: '*'
          destinationAddressPrefix: '*'
          destinationPortRange: '*'
        }
      }
    ]
  }
}

var accessSubnets = createOwnedAccess ? concat(managedEdge ? [
  {
    name: 'AzureFirewallSubnet'
    properties: {
      addressPrefix: firewallPrefix
      defaultOutboundAccess: false
    }
  }
  {
    name: 'GatewaySubnet'
    properties: {
      addressPrefix: gatewayPrefix
      defaultOutboundAccess: false
      routeTable: { id: gatewayRoutes.id }
    }
  }
] : [
  {
    name: 'snet-egress'
    properties: {
      addressPrefix: egressPrefix
      defaultOutboundAccess: false
      networkSecurityGroup: { id: nvaNsg!.id }
    }
  }
], [
  {
    name: 'snet-operator'
    properties: {
      addressPrefix: operatorPrefix
      defaultOutboundAccess: false
      routeTable: { id: ownedRoutes.id }
      networkSecurityGroup: { id: accessNsg.id }
    }
  }
  {
    name: 'snet-test-client'
    properties: {
      addressPrefix: clientPrefix
      defaultOutboundAccess: false
      routeTable: { id: ownedRoutes.id }
      networkSecurityGroup: { id: accessNsg.id }
    }
  }
  {
    name: 'AzureBastionSubnet'
    properties: {
      addressPrefix: bastionPrefix
      defaultOutboundAccess: false
    }
  }
]) : []

resource vnet 'Microsoft.Network/virtualNetworks@2024-07-01' = {
  name: 'vnet-${name}'
  location: location
  tags: tags
  properties: {
    dhcpOptions: configureCustomDns ? { dnsServers: [dnsProxyAddress] } : {}
    addressSpace: {
      addressPrefixes: [addressPrefix]
    }
    subnets: concat(integratedSubnets, [
      {
        name: 'snet-private-endpoints'
        properties: {
          addressPrefix: endpointsPrefix
          defaultOutboundAccess: false
          privateEndpointNetworkPolicies: 'Enabled'
          networkSecurityGroup: {
            id: endpointNsg.id
          }
        }
      }
    ], accessSubnets)
  }
}

var zoneNames = [
  'privatelink.blob.${environment().suffixes.storage}'
  'privatelink.queue.${environment().suffixes.storage}'
  'privatelink.table.${environment().suffixes.storage}'
  'privatelink${environment().suffixes.sqlServerHostname}'
  'privatelink.vaultcore.azure.net'
  'privatelink.azurecr.io'
  'privatelink.search.windows.net'
  'privatelink.openai.azure.com'
  'privatelink.cognitiveservices.azure.com'
  'privatelink.services.ai.azure.com'
  'privatelink.azurewebsites.net'
  'privatelink.monitor.azure.com'
  'privatelink.oms.opinsights.azure.com'
  'privatelink.ods.opinsights.azure.com'
  'privatelink.agentsvc.azure-automation.net'
]

resource zones 'Microsoft.Network/privateDnsZones@2020-06-01' = [for zoneName in zoneNames: {
  name: zoneName
  location: 'global'
  tags: tags
}]

resource links 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = [for (zoneName, i) in zoneNames: {
  parent: zones[i]
  name: 'link-${name}'
  location: 'global'
  tags: tags
  properties: {
    registrationEnabled: false
    virtualNetwork: {
      id: vnet.id
    }
  }
}]

output id string = vnet.id
output containerAppsSubnetId string = resourceId('Microsoft.Network/virtualNetworks/subnets', vnet.name, workloadSubnets[0].name)
output workflowsSubnetId string = resourceId('Microsoft.Network/virtualNetworks/subnets', vnet.name, workloadSubnets[1].name)
output operationsSubnetId string = resourceId('Microsoft.Network/virtualNetworks/subnets', vnet.name, workloadSubnets[2].name)
output endpointsSubnetId string = resourceId('Microsoft.Network/virtualNetworks/subnets', vnet.name, 'snet-private-endpoints')
output accessSubnets object = createOwnedAccess ? union({
  operator: resourceId('Microsoft.Network/virtualNetworks/subnets', vnet.name, 'snet-operator')
  client: resourceId('Microsoft.Network/virtualNetworks/subnets', vnet.name, 'snet-test-client')
  bastion: resourceId('Microsoft.Network/virtualNetworks/subnets', vnet.name, 'AzureBastionSubnet')
}, managedEdge ? {
  firewall: resourceId('Microsoft.Network/virtualNetworks/subnets', vnet.name, 'AzureFirewallSubnet')
  gateway: resourceId('Microsoft.Network/virtualNetworks/subnets', vnet.name, 'GatewaySubnet')
} : {
  egress: resourceId('Microsoft.Network/virtualNetworks/subnets', vnet.name, 'snet-egress')
}) : {}
output routeTableId string = routeTableId
output gatewayRouteTableId string = managedEdge ? gatewayRoutes!.id : ''
output managedEdgeBound bool = managedEdgeBound
output dns object = {
  blob: zones[0].id
  queue: zones[1].id
  table: zones[2].id
  sql: zones[3].id
  vault: zones[4].id
  registry: zones[5].id
  search: zones[6].id
  foundry: [zones[7].id, zones[8].id, zones[9].id]
  functions: zones[10].id
  monitor: [zones[11].id, zones[12].id, zones[13].id, zones[14].id, zones[0].id]
}
