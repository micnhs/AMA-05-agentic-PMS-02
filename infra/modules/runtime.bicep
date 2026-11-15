@description('Short workload name with deterministic suffix.')
param name string

@description('Approved EU candidate region.')
param location string

@description('Container Apps environment resource ID.')
param environmentId string

@description('Registry FQDN and immutable API/AgentHost image references.')
param images object

@description('Dedicated identity objects keyed api, agents, workflows, operations; each has id and clientId.')
param identities object

@description('Explicit tenant/app registrations, audiences and scopes. No credentials.')
param entra object

@description('Object-ID-to-product-ID array map for human permissions. Empty grants no product access.')
param productGrants object

@description('Explicit AgentHost product allowlist.')
param agentProducts string[]

@description('Server/database connection without User ID, Password or Authentication; TokenCallback provides the token.')
param sqlConnection string

@description('Azure Blob evidence container URI.')
param evidenceContainerUri string

@description('Search endpoint and index name.')
param search object

@description('OpenAI-compatible endpoint, pinned deployment and model version. ProcessingBoundary=EU requires separate actual geography verification.')
param model object

@description('Dedicated Flex subnets, endpoint subnet and Functions private DNS zone.')
param network object

@description('Host accounts and dedicated package-container URIs for both Functions.')
param functionStorage object

@description('Runtime catalog value for .NET 10 isolated, separately verified before deployment.')
param functionRuntimeVersion string

@description('Verified Operations timer function name for the always-ready scaling group. Must match generated functions.metadata.')
param operationsFunctionName string

@description('Name of the workspace-based Application Insights component.')
param insightsName string

@description('Non-sensitive cost and ownership tags.')
param tags object

resource insights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: insightsName
}

var commonSettings = {
  Cohors__LocalOperations: 'false'
  Cohors__AutoMigrate: 'false'
  Cohors__Seed: 'false'
  Cohors__PublicSources: 'false'
  APPLICATIONINSIGHTS_CONNECTION_STRING: insights.properties.ConnectionString
}

var productSettings = toObject(range(0, length(agentProducts)), i => 'Agents__Products__${i}', i => agentProducts[i])
var countrySettings = toObject(range(0, length(model.boundaryReview.processingCountries)), i => 'Foundry__BoundaryReview__ProcessingCountries__${i}', i => model.boundaryReview.processingCountries[i])

module agents 'container-app.bicep' = {
  name: 'agents-${name}'
  params: {
    name: 'ca-${name}-agents'
    location: location
    environmentId: environmentId
    image: images.agents
    registryServer: images.registry
    identityResourceId: identities.agents.id
    accessibleFromVnet: false
    settings: union(commonSettings, productSettings, countrySettings, {
      ASPNETCORE_ENVIRONMENT: 'Production'
      ASPNETCORE_HTTP_PORTS: '8080'
      COHORS_HOME: '/app'
      Authentication__Mode: 'Entra'
      Entra__TenantId: entra.tenantId
      Entra__Audience: entra.agentsAudience
      Agents__AllowedCallerClientId: identities.api.clientId
      AZURE_CLIENT_ID: identities.agents.clientId
      Foundry__Endpoint: model.endpoint
      Foundry__Deployment: model.deployment
      Foundry__ModelVersion: model.version
      Foundry__ProcessingBoundary: 'EU-Member-States'
      Foundry__BoundaryReview__Reference: model.boundaryReview.reference
      Foundry__BoundaryReview__ConfigurationHash: model.boundaryReview.configurationHash
      Foundry__BoundaryReview__DeploymentSku: model.boundaryReview.deploymentSku
      Foundry__BoundaryReview__DeploymentRegion: model.boundaryReview.deploymentRegion
      Foundry__BoundaryReview__DeploymentGeography: model.boundaryReview.deploymentGeography
      APPLICATIONINSIGHTS_AUTHENTICATION_STRING: 'ClientId=${identities.agents.clientId};Authorization=AAD'
    })
    tags: tags
  }
}

var grantSettings = flatten(map(items(productGrants), grant => map(range(0, length(grant.value)), i => {
  key: 'Entra__ProductGrants__${grant.key}__${i}'
  value: grant.value[i]
})))

module api 'container-app.bicep' = {
  name: 'api-${name}'
  params: {
    name: 'ca-${name}-api'
    location: location
    environmentId: environmentId
    image: images.api
    registryServer: images.registry
    identityResourceId: identities.api.id
    accessibleFromVnet: true
    settings: union(commonSettings, toObject(grantSettings, grant => grant.key, grant => grant.value), {
      ASPNETCORE_ENVIRONMENT: 'Production'
      ASPNETCORE_HTTP_PORTS: '8080'
      COHORS_HOME: '/app'
      Authentication__Mode: 'Entra'
      Entra__TenantId: entra.tenantId
      Entra__Audience: entra.apiAudience
      Entra__SpaClientId: entra.spaClientId
      Entra__Scope: entra.delegatedScope
      Cohors__SqlAuthentication: 'ManagedIdentity'
      ConnectionStrings__Cohors: sqlConnection
      Cohors__ObjectStore: 'Azure'
      Blob__ContainerUri: evidenceContainerUri
      Cohors__Search: 'Azure'
      Search__Endpoint: search.endpoint
      Search__Index: search.index
      Cohors__AgentTransport: 'Remote'
      Agents__Endpoint: agents.outputs.url
      Agents__Scope: entra.agentScope
      AZURE_CLIENT_ID: identities.api.clientId
      APPLICATIONINSIGHTS_AUTHENTICATION_STRING: 'ClientId=${identities.api.clientId};Authorization=AAD'
    })
    tags: tags
  }
}

module workflows 'flex-function.bicep' = {
  name: 'workflows-${name}'
  params: {
    name: 'func-${name}-workflows'
    location: location
    subnetId: network.workflowsSubnetId
    endpointSubnetId: network.endpointSubnetId
    privateDnsZoneId: network.functionsDnsZoneId
    identityResourceId: identities.workflows.id
    identityClientId: identities.workflows.clientId
    hostStorageAccountName: functionStorage.workflowsAccount
    packageContainerUrl: functionStorage.workflowsPackageUri
    runtimeVersion: functionRuntimeVersion
    alwaysReady: [
      { name: 'durable', instanceCount: 1 }
      { name: 'http', instanceCount: 1 }
    ]
    settings: union(commonSettings, {
      Cohors__ApiBaseUrl: api.outputs.url
      Cohors__ApiScope: entra.workflowScope
      AzureFunctionsJobHost__extensions__durableTask__hubName: 'CohorsPilotV1'
      APPLICATIONINSIGHTS_AUTHENTICATION_STRING: 'ClientId=${identities.workflows.clientId};Authorization=AAD'
    })
    tags: tags
  }
}

module operations 'flex-function.bicep' = {
  name: 'operations-${name}'
  params: {
    name: 'func-${name}-operations'
    location: location
    subnetId: network.operationsSubnetId
    endpointSubnetId: network.endpointSubnetId
    privateDnsZoneId: network.functionsDnsZoneId
    identityResourceId: identities.operations.id
    identityClientId: identities.operations.clientId
    hostStorageAccountName: functionStorage.operationsAccount
    packageContainerUrl: functionStorage.operationsPackageUri
    runtimeVersion: functionRuntimeVersion
    alwaysReady: [
      { name: 'function:${operationsFunctionName}', instanceCount: 1 }
    ]
    settings: union(commonSettings, {
      Cohors__SqlAuthentication: 'ManagedIdentity'
      ConnectionStrings__Cohors: sqlConnection
      APPLICATIONINSIGHTS_AUTHENTICATION_STRING: 'ClientId=${identities.operations.clientId};Authorization=AAD'
    })
    tags: tags
  }
}

output apiUrl string = api.outputs.url
output agentsUrl string = agents.outputs.url
output workflowsUrl string = workflows.outputs.url
output operationsUrl string = operations.outputs.url
