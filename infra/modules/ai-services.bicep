@description('Globally unique search service name.')
param searchName string

@description('Foundry account and custom subdomain name.')
param foundryName string

@description('Approved EU candidate region; not a verification of model geography or availability.')
param location string

@description('Private-Link capable search SKU candidate.')
@allowed(['basic', 'standard'])
param searchSku string

@description('Search replicas. One is only a non-HA pilot; production read/write needs a separately reviewed configuration.')
@minValue(1)
@maxValue(3)
param searchReplicas int

@description('API identity object ID for query and push indexing; no Search management role.')
param searchPrincipalId string

@description('Only identity object IDs allowed to invoke the approved model.')
param inferencePrincipalIds string[]

@description('Do not create a paid model deployment until the model-specific EU/availability/quota/cost gate is approved.')
param deployModel bool = false

@description('Explicit approved OpenAI model name. Empty is valid only when deployModel=false.')
param modelName string = ''

@description('Pinned model version, never latest or an omitted provider default.')
param modelVersion string = ''

@description('Model deployment name used by the runtime.')
param modelDeploymentName string = 'cohors-model'

@description('Model-specific capacity units, not assumed tokens-per-minute or available quota.')
@minValue(1)
param modelCapacity int = 1

@description('Private endpoint subnet resource ID.')
param endpointSubnetId string

@description('Search and Foundry private DNS zone resource IDs.')
param dns object

@description('Log Analytics workspace resource ID for metrics only.')
param workspaceId string

@description('Non-sensitive cost and ownership tags.')
param tags object

resource search 'Microsoft.Search/searchServices@2025-05-01' = {
  name: searchName
  location: location
  tags: tags
  sku: {
    name: searchSku
  }
  properties: {
    replicaCount: searchReplicas
    partitionCount: 1
    hostingMode: 'Default'
    semanticSearch: 'disabled'
    disableLocalAuth: true
    publicNetworkAccess: 'Disabled'
    networkRuleSet: {
      bypass: 'None'
      ipRules: []
    }
    dataExfiltrationProtections: ['BlockAll']
  }
}

resource indexing 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(search.id, searchPrincipalId, 'Search Index Data Contributor')
  scope: search
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '8ebe5a00-799e-43f5-93ac-243d3dce84a7')
    principalId: searchPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource foundry 'Microsoft.CognitiveServices/accounts@2025-06-01' = {
  name: foundryName
  location: location
  tags: tags
  kind: 'AIServices'
  sku: {
    name: 'S0'
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    customSubDomainName: foundryName
    disableLocalAuth: true
    publicNetworkAccess: 'Disabled'
    restrictOutboundNetworkAccess: true
    allowedFqdnList: []
    networkAcls: {
      defaultAction: 'Deny'
      bypass: 'None'
      ipRules: []
      virtualNetworkRules: []
    }
  }
}

resource inference 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for principalId in inferencePrincipalIds: {
  name: guid(foundry.id, principalId, 'Cognitive Services OpenAI User')
  scope: foundry
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}]

resource deployment 'Microsoft.CognitiveServices/accounts/deployments@2025-06-01' = if (deployModel) {
  parent: foundry
  name: modelDeploymentName
  sku: {
    name: 'Standard'
    capacity: modelCapacity
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: modelName
      version: modelVersion
    }
    versionUpgradeOption: 'NoAutoUpgrade'
  }
}

resource searchMetrics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'metrics-only'
  scope: search
  properties: {
    workspaceId: workspaceId
    metrics: [
      { category: 'AllMetrics', enabled: true }
    ]
  }
}

resource modelMetrics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'metrics-only'
  scope: foundry
  properties: {
    workspaceId: workspaceId
    metrics: [
      { category: 'AllMetrics', enabled: true }
    ]
  }
}

module searchEndpoint 'private-endpoint.bicep' = {
  name: 'pe-${searchName}'
  params: {
    name: 'pe-${searchName}'
    location: location
    subnetId: endpointSubnetId
    targetResourceId: search.id
    groupId: 'searchService'
    privateDnsZoneIds: [dns.search]
    tags: tags
  }
}

module foundryEndpoint 'private-endpoint.bicep' = {
  name: 'pe-${foundryName}'
  params: {
    name: 'pe-${foundryName}'
    location: location
    subnetId: endpointSubnetId
    targetResourceId: foundry.id
    groupId: 'account'
    privateDnsZoneIds: dns.foundry
    tags: tags
  }
}

output searchId string = search.id
output searchEndpoint string = 'https://${search.name}.search.windows.net'
output foundryId string = foundry.id
output openAiEndpoint string = 'https://${foundry.properties.customSubDomainName}.openai.azure.com/'
