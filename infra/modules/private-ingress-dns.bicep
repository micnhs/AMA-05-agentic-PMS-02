@description('Short workload name with deterministic suffix.')
param name string

@description('Container Apps generated default domain, resolved only after environment creation.')
param domain string

@description('Container Apps internal environment static IP.')
param privateIp string

@description('VNet resource ID.')
param vnetId string

@description('Non-sensitive cost and ownership tags.')
param tags object

resource zone 'Microsoft.Network/privateDnsZones@2020-06-01' = {
  name: domain
  location: 'global'
  tags: tags
}

resource link 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = {
  parent: zone
  name: 'link-${name}'
  location: 'global'
  tags: tags
  properties: {
    registrationEnabled: false
    virtualNetwork: { id: vnetId }
  }
}

resource records 'Microsoft.Network/privateDnsZones/A@2020-06-01' = [for record in ['@', '*']: {
  parent: zone
  name: record
  properties: {
    ttl: 300
    aRecords: [
      { ipv4Address: privateIp }
    ]
  }
}]
