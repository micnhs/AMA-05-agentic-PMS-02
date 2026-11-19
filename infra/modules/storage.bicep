@description('Globally unique lowercase alphanumeric account name, 3-24 characters.')
@minLength(3)
@maxLength(24)
param name string

@description('Approved EU candidate region.')
param location string

@description('In-region redundancy; choose only after regional capability and cost review.')
@allowed(['Standard_LRS', 'Standard_ZRS'])
param sku string

@description('Containers with distinct access and retention purposes.')
param containers string[] = []

@description('Blob recovery protection for evidence/artifacts; disabled for mutable Function/Durable host state.')
param protectBlobVersions bool = false

@description('Account-level data roles for the dedicated host only: principalId and roleDefinitionId (GUID).')
param accountRoles array = []

@description('Container-level grants: container, principalId and roleDefinitionId (GUID).')
param containerRoles array = []

@description('Include queue/table private endpoints for Function host and Durable access.')
param includeQueueAndTable bool = false

@description('Private endpoint subnet resource ID.')
param endpointSubnetId string

@description('Private DNS zone IDs keyed by blob, queue and table.')
param dns object
@description('Shared-hub consumer mode never writes or joins shared private DNS zones.')
param managePrivateDns bool = true

@description('Log Analytics workspace resource ID for metrics only.')
param workspaceId string

@description('Non-sensitive cost and ownership tags.')
param tags object

resource account 'Microsoft.Storage/storageAccounts@2025-01-01' = {
  name: name
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: {
    name: sku
  }
  properties: {
    accessTier: 'Hot'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    allowCrossTenantReplication: false
    defaultToOAuthAuthentication: true
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
    publicNetworkAccess: 'Disabled'
    networkAcls: {
      defaultAction: 'Deny'
      bypass: 'None'
      ipRules: []
      virtualNetworkRules: []
    }
    encryption: {
      keySource: 'Microsoft.Storage'
      requireInfrastructureEncryption: true
      services: {
        blob: { enabled: true, keyType: 'Account' }
        file: { enabled: true, keyType: 'Account' }
        queue: { enabled: true, keyType: 'Account' }
        table: { enabled: true, keyType: 'Account' }
      }
    }
  }
}

resource blob 'Microsoft.Storage/storageAccounts/blobServices@2025-01-01' = {
  parent: account
  name: 'default'
  properties: protectBlobVersions ? {
    isVersioningEnabled: true
    deleteRetentionPolicy: { enabled: true, days: 14 }
    containerDeleteRetentionPolicy: { enabled: true, days: 14 }
  } : {
    isVersioningEnabled: false
  }
}

resource blobContainers 'Microsoft.Storage/storageAccounts/blobServices/containers@2025-01-01' = [for container in containers: {
  parent: blob
  name: container
  properties: {
    publicAccess: 'None'
  }
}]

resource hostRoles 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for grant in accountRoles: {
  name: guid(account.id, grant.principalId, grant.roleDefinitionId)
  scope: account
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', grant.roleDefinitionId)
    principalId: grant.principalId
    principalType: 'ServicePrincipal'
  }
}]

module scopedRoles 'container-role.bicep' = [for grant in containerRoles: {
  name: 'grant-${uniqueString(name, grant.container, grant.principalId, grant.roleDefinitionId)}'
  params: {
    accountName: account.name
    containerName: grant.container
    principalId: grant.principalId
    roleDefinitionGuid: grant.roleDefinitionId
  }
  dependsOn: [blobContainers]
}]

var services = includeQueueAndTable ? ['blob', 'queue', 'table'] : ['blob']
module endpoints 'private-endpoint.bicep' = [for service in services: {
  name: 'pe-${name}-${service}'
  params: {
    name: 'pe-${name}-${service}'
    location: location
    subnetId: endpointSubnetId
    targetResourceId: account.id
    groupId: service
    privateDnsZoneIds: [dns[service]]
    managePrivateDns: managePrivateDns
    tags: tags
  }
}]

resource metrics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'metrics-only'
  scope: account
  properties: {
    workspaceId: workspaceId
    metrics: [
      { category: 'Transaction', enabled: true }
    ]
  }
}

output id string = account.id
output name string = account.name
output blobEndpoint string = account.properties.primaryEndpoints.blob
output queueEndpoint string = account.properties.primaryEndpoints.queue
output tableEndpoint string = account.properties.primaryEndpoints.table
