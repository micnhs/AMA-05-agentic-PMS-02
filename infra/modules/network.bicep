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

@description('Non-sensitive cost and ownership tags.')
param tags object

var workloadSubnets = [
  { name: 'snet-container-apps', prefix: containerAppsPrefix }
  { name: 'snet-workflows', prefix: workflowsPrefix }
  { name: 'snet-operations', prefix: operationsPrefix }
]

resource ownedRoutes 'Microsoft.Network/routeTables@2024-07-01' = if (createOwnedAccess) {
  name: 'rt-${name}-filtered'
  location: location
  tags: tags
  properties: {
    disableBgpRoutePropagation: true
    routes: [
      {
        name: 'default-via-own-filter'
        properties: {
          addressPrefix: '0.0.0.0/0'
          nextHopType: 'VirtualAppliance'
          nextHopIpAddress: nvaPrivateIp
        }
      }
    ]
  }
}

var routeTableId = createOwnedAccess ? ownedRoutes.id : egressRouteTableResourceId
var filteredOutbound = [
  {
    name: 'AllowOwnDns'
    properties: {
      priority: 100
      direction: 'Outbound'
      access: 'Allow'
      protocol: '*'
      sourceAddressPrefix: '*'
      sourcePortRange: '*'
      destinationAddressPrefix: nvaPrivateIp
      destinationPortRange: '53'
    }
  }
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
    name: 'AllowTlsSubjectToNvaRoute'
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
]

resource workloadNsgs 'Microsoft.Network/networkSecurityGroups@2024-07-01' = [for subnet in workloadSubnets: {
  name: 'nsg-${name}-${replace(subnet.name, 'snet-', '')}'
  location: location
  tags: tags
  properties: {
    securityRules: createOwnedAccess ? filteredOutbound : []
  }
}]

resource endpointNsg 'Microsoft.Network/networkSecurityGroups@2024-07-01' = {
  name: 'nsg-${name}-endpoints'
  location: location
  tags: tags
  properties: {
    securityRules: [
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
    ]
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

resource nvaNsg 'Microsoft.Network/networkSecurityGroups@2024-07-01' = if (createOwnedAccess) {
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

var accessSubnets = createOwnedAccess ? [
  {
    name: 'snet-egress'
    properties: {
      addressPrefix: egressPrefix
      defaultOutboundAccess: false
      networkSecurityGroup: { id: nvaNsg.id }
    }
  }
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
] : []

resource vnet 'Microsoft.Network/virtualNetworks@2024-07-01' = {
  name: 'vnet-${name}'
  location: location
  tags: tags
  properties: {
    dhcpOptions: createOwnedAccess ? { dnsServers: [nvaPrivateIp] } : {}
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
output accessSubnets object = createOwnedAccess ? {
  egress: resourceId('Microsoft.Network/virtualNetworks/subnets', vnet.name, 'snet-egress')
  operator: resourceId('Microsoft.Network/virtualNetworks/subnets', vnet.name, 'snet-operator')
  client: resourceId('Microsoft.Network/virtualNetworks/subnets', vnet.name, 'snet-test-client')
  bastion: resourceId('Microsoft.Network/virtualNetworks/subnets', vnet.name, 'AzureBastionSubnet')
} : {}
output routeTableId string = routeTableId
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
