@description('Private endpoint name, without business or patient identifiers.')
param name string

@description('Same region as the private endpoint VNet.')
param location string

@description('Nondelegated private endpoint subnet resource ID.')
param subnetId string

@description('Target PaaS resource ID.')
param targetResourceId string

@description('Service-defined private link subresource, for example blob or sqlServer.')
param groupId string

@description('Authoritative private DNS zone resource IDs for this endpoint.')
@minLength(1)
param privateDnsZoneIds string[]

@description('Non-sensitive cost and ownership tags.')
param tags object

resource endpoint 'Microsoft.Network/privateEndpoints@2024-07-01' = {
  name: name
  location: location
  tags: tags
  properties: {
    subnet: {
      id: subnetId
    }
    privateLinkServiceConnections: [
      {
        name: '${name}-connection'
        properties: {
          privateLinkServiceId: targetResourceId
          groupIds: [groupId]
        }
      }
    ]
  }
}

resource dnsGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-07-01' = {
  parent: endpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [for i in range(0, length(privateDnsZoneIds)): {
      name: 'zone-${i}'
      properties: {
        privateDnsZoneId: privateDnsZoneIds[i]
      }
    }]
  }
}

output id string = endpoint.id
