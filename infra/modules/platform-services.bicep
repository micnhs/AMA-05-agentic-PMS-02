@description('Globally unique alphanumeric registry name.')
param registryName string

@description('Globally unique Key Vault name, at most 24 characters.')
param vaultName string

@description('Approved EU candidate region.')
param location string

@description('Only workload identity object IDs that pull approved containers.')
param pullPrincipalIds string[]

@description('Private endpoint subnet resource ID.')
param endpointSubnetId string

@description('Registry and vault private DNS zone resource IDs.')
param dns object

@description('Log Analytics workspace resource ID.')
param workspaceId string

@description('Non-sensitive cost and ownership tags.')
param tags object

resource registry 'Microsoft.ContainerRegistry/registries@2025-04-01' = {
  name: registryName
  location: location
  tags: tags
  sku: {
    name: 'Premium'
  }
  properties: {
    adminUserEnabled: false
    anonymousPullEnabled: false
    dataEndpointEnabled: true
    publicNetworkAccess: 'Disabled'
    networkRuleBypassOptions: 'None'
    networkRuleSet: {
      defaultAction: 'Deny'
      ipRules: []
    }
    policies: {
      exportPolicy: { status: 'disabled' }
    }
  }
}

resource pulls 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for principalId in pullPrincipalIds: {
  name: guid(registry.id, principalId, 'AcrPull')
  scope: registry
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}]

resource registryLogs 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'registry-security'
  scope: registry
  properties: {
    workspaceId: workspaceId
    logs: [
      { category: 'ContainerRegistryLoginEvents', enabled: true }
      { category: 'ContainerRegistryRepositoryEvents', enabled: true }
    ]
    metrics: [
      { category: 'AllMetrics', enabled: true }
    ]
  }
}

resource vault 'Microsoft.KeyVault/vaults@2024-11-01' = {
  name: vaultName
  location: location
  tags: tags
  properties: {
    tenantId: tenant().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    enableRbacAuthorization: true
    enableSoftDelete: true
    enablePurgeProtection: true
    softDeleteRetentionInDays: 90
    enabledForDeployment: false
    enabledForDiskEncryption: false
    enabledForTemplateDeployment: false
    publicNetworkAccess: 'Disabled'
    accessPolicies: []
    networkAcls: {
      defaultAction: 'Deny'
      bypass: 'None'
      ipRules: []
      virtualNetworkRules: []
    }
  }
}

resource vaultLogs 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'vault-security'
  scope: vault
  properties: {
    workspaceId: workspaceId
    logs: [
      { category: 'AuditEvent', enabled: true }
    ]
    metrics: [
      { category: 'AllMetrics', enabled: true }
    ]
  }
}

module registryEndpoint 'private-endpoint.bicep' = {
  name: 'pe-${registryName}'
  params: {
    name: 'pe-${registryName}'
    location: location
    subnetId: endpointSubnetId
    targetResourceId: registry.id
    groupId: 'registry'
    privateDnsZoneIds: [dns.registry]
    tags: tags
  }
}

module vaultEndpoint 'private-endpoint.bicep' = {
  name: 'pe-${vaultName}'
  params: {
    name: 'pe-${vaultName}'
    location: location
    subnetId: endpointSubnetId
    targetResourceId: vault.id
    groupId: 'vault'
    privateDnsZoneIds: [dns.vault]
    tags: tags
  }
}

output registryId string = registry.id
output registryLoginServer string = registry.properties.loginServer
output vaultId string = vault.id
output vaultUri string = vault.properties.vaultUri
