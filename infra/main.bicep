targetScope = 'resourceGroup'

@description('Short lowercase alphanumeric workload prefix. Names contain no medical or personal information.')
@minLength(3)
@maxLength(10)
param namePrefix string = 'cohors'

@description('EU-member-state location candidates only; region/SKU/model geography/quota/cost must still be verified for the target subscription.')
@allowed(['westeurope', 'northeurope', 'swedencentral', 'germanywestcentral', 'francecentral'])
param location string

@description('Explicit independently reviewed Foundry location. Default stays with the workload; no automatic regional fallback.')
@allowed(['westeurope', 'northeurope', 'swedencentral', 'germanywestcentral', 'francecentral'])
param foundryLocation string = location

@description('Existing reviewed egress route table; connectivity, UDRs, DNS forwarding and firewall allowlists remain platform prerequisites.')
param egressRouteTableResourceId string = ''

@description('Existing operational action group, with tested delivery outside the application failure domain.')
param actionGroupResourceId string = ''

@description('The automatic primary bootstrap creates its own route table, access subnets and action group.')
param createOwnedAccess bool = false

@description('Generated network-phase outputs. Empty retains the original standalone network module; never a manually prebuilt target requirement.')
param networkResourceBindings object = {}

@description('Explicitly reviewed operational email receivers; never infer recipients from the subscription owner.')
param alertEmailReceivers array = []

@description('Own access-network allocation, bound by the bootstrap manifest and CIDR review.')
param nvaPrivateIp string = '10.42.4.4'
param egressSubnetPrefix string = '10.42.4.0/27'
param operatorSubnetPrefix string = '10.42.4.32/27'
param clientSubnetPrefix string = '10.42.4.64/27'
param bastionSubnetPrefix string = '10.42.5.0/26'

@description('Existing Entra SQL administrator GROUP object ID, not a runtime or CI principal.')
@minLength(36)
@maxLength(36)
param sqlAdminGroupObjectId string

@description('SQL administrative group display name; no individual administrator or password.')
param sqlAdminGroupName string

@description('Reviewed nonoverlapping IPv4 network allocation.')
param vnetPrefix string = '10.42.0.0/16'
param containerAppsSubnetPrefix string = '10.42.0.0/23'
param workflowsSubnetPrefix string = '10.42.2.0/26'
param operationsSubnetPrefix string = '10.42.2.64/26'
param privateEndpointsSubnetPrefix string = '10.42.3.0/24'

@description('Explicit in-region redundancy candidate; no geo replication.')
@allowed(['Standard_LRS', 'Standard_ZRS'])
param storageSku string = 'Standard_ZRS'

@description('SQL candidate capacity, not a capacity/quota or cost assertion.')
@allowed([2, 4, 8])
param sqlVcores int = 2

@description('Database maximum size in GiB.')
@minValue(2)
@maxValue(128)
param sqlMaxSizeGiB int = 32

@description('Enable SQL zone redundancy only after regional/SKU/cost validation.')
param sqlZoneRedundant bool = false

@allowed(['Local', 'Zone'])
param sqlBackupRedundancy string = 'Local'

@minValue(7)
@maxValue(35)
param sqlPitrDays int = 14

@description('Optional server-level Defender/Threat Detection policy, absent by default. Does not disable existing/inherited protection or onboard subscription-wide pricing. Separate cost, statement-content/privacy, EU, access/retention and SecOps reviews are mandatory before activation.')
param deploySqlThreatProtection bool = false

@description('Explicit reviewed SecOps notification addresses only. Never automatically notify all account administrators.')
param sqlSecurityNotificationEmails string[] = []

@allowed(['basic', 'standard'])
param searchSku string = 'basic'

@minValue(1)
@maxValue(3)
param searchReplicas int = 1

@description('Production HA requires a separately reviewed zonal/replica strategy.')
param containerAppsZoneRedundant bool = false

@description('Consumption retains the historical candidate. D4 is an explicit additional-cost strict-DNS alternative, with one dedicated node and no HA claim.')
@allowed(['Consumption', 'D4'])
param containerAppsWorkloadProfileType string = 'Consumption'

@description('No model is deployed by default. Standard only; never Global, DataZone, auto-upgrade or spillover.')
param deployModel bool = false
param modelName string = ''
param modelVersion string = ''
param modelDeploymentName string = 'cohors-model'

@description('Controlled provider review: reference, configurationHash, deploymentSku, deploymentRegion, deploymentGeography, processingCountries. Hash must come from AgentHost --boundary-hash, not ARM stringification.')
param foundryBoundaryReview object = {}

@minValue(1)
param modelCapacity int = 1

@description('Foundation only by default. Enable only after immutable images, packages, Entra grants, SQL grants/migration and all deployment gates are reviewed.')
param deployRuntime bool = false

@description('64-character cohors-api sha256 digest in this workload registry, without the sha256: prefix.')
@maxLength(64)
param apiImageDigest string = ''

@description('64-character cohors-agents sha256 digest in this workload registry. Separate host, not in-process API agents.')
@maxLength(64)
param agentsImageDigest string = ''

@description('Entra application IDs/audiences/scopes; all values are required when runtime is enabled.')
param entraTenantId string = tenant().tenantId
param apiAudience string = ''
param agentsAudience string = ''
param spaClientId string = ''
param spaDelegatedScope string = ''
param workflowApiScope string = ''
param agentsApiScope string = ''

@description('Human object IDs mapped to allowed product IDs; empty means no product authorization.')
param productGrants object = {}

@description('Explicit product IDs accepted by the separate AgentHost. Empty denies product processing.')
param agentProducts string[] = []

@description('Index must be provisioned by the controlled release identity, not silently by runtime management rights.')
param searchIndexName string = 'cohors-evidence'

@allowed(['10.0'])
param functionRuntimeVersion string = '10.0'

@description('Must match the Operations timer function name in generated functions.metadata; no invented trigger name.')
param operationsFunctionName string = 'DeadlineWatchdog'

@description('Success telemetry contract and action group delivery must be verified; disabled means no operational alarm protection.')
param enableWatchdogAlert bool = false

@description('Application emitter cloud role; no clinical/user-derived names.')
@allowed(['cohors-operations'])
param operationsTelemetryRole string = 'cohors-operations'

@description('No writer grant or running digest publisher by default. Grant only after the independent publisher/retention/digest verification design is approved.')
param enableDigestPublisherGrant bool = false

@description('Non-sensitive workload/cost tags; do not add case names, individual identities or medical content.')
param tags object = {
  workload: 'cohors'
  environment: 'synthetic-pilot'
  dataClassification: 'synthetic'
}

var suffix = take(uniqueString(subscription().subscriptionId, resourceGroup().id, namePrefix), 8)
var name = '${namePrefix}-${suffix}'
var compactName = '${namePrefix}${suffix}'
var identityNames = ['api', 'agents', 'workflows', 'operations', 'digest-publisher', 'digest-verifier', 'alerts']
var blobOwnerRole = 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'
var blobContributorRole = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
var blobReaderRole = '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1'
var queueContributorRole = '974c5e8b-45b9-4653-ba55-5f855dd0fb88'
var tableContributorRole = '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'
var functionHostRoles = [blobOwnerRole, queueContributorRole, tableContributorRole]

resource identities 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = [for role in identityNames: {
  name: 'id-${name}-${role}'
  location: location
  tags: tags
}]

module network 'modules/network.bicep' = if (empty(networkResourceBindings)) {
  name: 'network-${name}'
  params: {
    name: name
    location: location
    addressPrefix: vnetPrefix
    containerAppsPrefix: containerAppsSubnetPrefix
    workflowsPrefix: workflowsSubnetPrefix
    operationsPrefix: operationsSubnetPrefix
    endpointsPrefix: privateEndpointsSubnetPrefix
    egressRouteTableResourceId: egressRouteTableResourceId
    createOwnedAccess: createOwnedAccess
    nvaPrivateIp: nvaPrivateIp
    egressPrefix: egressSubnetPrefix
    operatorPrefix: operatorSubnetPrefix
    clientPrefix: clientSubnetPrefix
    bastionPrefix: bastionSubnetPrefix
    tags: tags
  }
}

var networkData = !empty(networkResourceBindings) ? networkResourceBindings : {
  id: network!.outputs.id
  endpointsSubnetId: network!.outputs.endpointsSubnetId
  containerAppsSubnetId: network!.outputs.containerAppsSubnetId
  workflowsSubnetId: network!.outputs.workflowsSubnetId
  operationsSubnetId: network!.outputs.operationsSubnetId
  accessSubnets: network!.outputs.accessSubnets
  routeTableId: network!.outputs.routeTableId
  dns: network!.outputs.dns
}

resource ownedActionGroup 'Microsoft.Insights/actionGroups@2023-01-01' = if (createOwnedAccess) {
  name: 'ag-${name}-operations'
  location: 'global'
  tags: tags
  properties: {
    groupShortName: 'CohorsOps'
    enabled: true
    emailReceivers: [for (receiver, i) in alertEmailReceivers: {
      name: 'receiver-${i}'
      emailAddress: receiver
      useCommonAlertSchema: true
    }]
  }
}

module monitoring 'modules/monitoring.bicep' = {
  name: 'monitoring-${name}'
  params: {
    name: name
    location: location
    telemetryPrincipalIds: [for i in range(0, 4): identities[i].properties.principalId]
    alertIdentityResourceId: identities[6].id
    alertPrincipalId: identities[6].properties.principalId
    actionGroupResourceId: createOwnedAccess ? ownedActionGroup.id : actionGroupResourceId
    enableWatchdogAlert: enableWatchdogAlert
    operationsRoleName: operationsTelemetryRole
    endpointSubnetId: networkData.endpointsSubnetId
    privateDnsZoneIds: networkData.dns.monitor
    tags: tags
  }
}

module platform 'modules/platform-services.bicep' = {
  name: 'platform-${name}'
  params: {
    registryName: 'cr${compactName}'
    vaultName: 'kv-${name}'
    location: location
    pullPrincipalIds: [identities[0].properties.principalId, identities[1].properties.principalId]
    endpointSubnetId: networkData.endpointsSubnetId
    dns: networkData.dns
    workspaceId: monitoring.outputs.workspaceId
    tags: tags
  }
}

module evidence 'modules/storage.bicep' = {
  name: 'evidence-${name}'
  params: {
    name: 'st${compactName}ev'
    location: location
    sku: storageSku
    containers: ['quarantine', 'evidence', 'approved-packages']
    protectBlobVersions: true
    containerRoles: [
      {
        container: 'evidence'
        principalId: identities[0].properties.principalId
        roleDefinitionId: blobContributorRole
      }
    ]
    endpointSubnetId: networkData.endpointsSubnetId
    dns: networkData.dns
    workspaceId: monitoring.outputs.workspaceId
    tags: tags
  }
}

module workflowStorage 'modules/storage.bicep' = {
  name: 'workflow-storage-${name}'
  params: {
    name: 'st${compactName}wf'
    location: location
    sku: storageSku
    includeQueueAndTable: true
    accountRoles: [for role in functionHostRoles: {
      principalId: identities[2].properties.principalId
      roleDefinitionId: role
    }]
    endpointSubnetId: networkData.endpointsSubnetId
    dns: networkData.dns
    workspaceId: monitoring.outputs.workspaceId
    tags: tags
  }
}

module operationsStorage 'modules/storage.bicep' = {
  name: 'operations-storage-${name}'
  params: {
    name: 'st${compactName}op'
    location: location
    sku: storageSku
    includeQueueAndTable: true
    accountRoles: [for role in functionHostRoles: {
      principalId: identities[3].properties.principalId
      roleDefinitionId: role
    }]
    endpointSubnetId: networkData.endpointsSubnetId
    dns: networkData.dns
    workspaceId: monitoring.outputs.workspaceId
    tags: tags
  }
}

module packages 'modules/storage.bicep' = {
  name: 'packages-${name}'
  params: {
    name: 'st${compactName}pkg'
    location: location
    sku: storageSku
    containers: ['workflows-package', 'operations-package']
    protectBlobVersions: true
    containerRoles: [
      { container: 'workflows-package', principalId: identities[2].properties.principalId, roleDefinitionId: blobContributorRole }
      { container: 'operations-package', principalId: identities[3].properties.principalId, roleDefinitionId: blobContributorRole }
    ]
    endpointSubnetId: networkData.endpointsSubnetId
    dns: networkData.dns
    workspaceId: monitoring.outputs.workspaceId
    tags: tags
  }
}

module digests 'modules/storage.bicep' = {
  name: 'digests-${name}'
  params: {
    name: 'st${compactName}dg'
    location: location
    sku: 'Standard_LRS'
    containers: ['ledger-digests']
    protectBlobVersions: true
    containerRoles: concat([
      { container: 'ledger-digests', principalId: identities[5].properties.principalId, roleDefinitionId: blobReaderRole }
    ], enableDigestPublisherGrant ? [
      { container: 'ledger-digests', principalId: identities[4].properties.principalId, roleDefinitionId: blobContributorRole }
    ] : [])
    endpointSubnetId: networkData.endpointsSubnetId
    dns: networkData.dns
    workspaceId: monitoring.outputs.workspaceId
    tags: tags
  }
}

module sql 'modules/sql.bicep' = {
  name: 'sql-${name}'
  params: {
    name: 'sql-${name}'
    databaseName: 'Cohors'
    location: location
    adminGroupObjectId: sqlAdminGroupObjectId
    adminGroupName: sqlAdminGroupName
    vCores: sqlVcores
    maxSizeGiB: sqlMaxSizeGiB
    zoneRedundant: sqlZoneRedundant
    backupRedundancy: sqlBackupRedundancy
    backupRetentionDays: sqlPitrDays
    deployThreatProtection: deploySqlThreatProtection
    securityNotificationEmails: sqlSecurityNotificationEmails
    endpointSubnetId: networkData.endpointsSubnetId
    privateDnsZoneId: networkData.dns.sql
    workspaceId: monitoring.outputs.workspaceId
    tags: tags
  }
}

module ai 'modules/ai-services.bicep' = {
  name: 'ai-${name}'
  params: {
    foundryLocation: foundryLocation
    searchName: 'srch-${name}'
    foundryName: 'aif-${name}'
    location: location
    searchSku: searchSku
    searchReplicas: searchReplicas
    searchPrincipalId: identities[0].properties.principalId
    inferencePrincipalIds: [identities[1].properties.principalId]
    deployModel: deployModel
    modelName: modelName
    modelVersion: modelVersion
    modelDeploymentName: modelDeploymentName
    modelCapacity: modelCapacity
    endpointSubnetId: networkData.endpointsSubnetId
    dns: networkData.dns
    workspaceId: monitoring.outputs.workspaceId
    tags: tags
  }
}

module containerEnvironment 'modules/container-environment.bicep' = {
  name: 'containers-${name}'
  params: {
    name: name
    location: location
    subnetId: networkData.containerAppsSubnetId
    vnetId: networkData.id
    workspaceId: monitoring.outputs.workspaceId
    zoneRedundant: containerAppsZoneRedundant
    workloadProfileType: containerAppsWorkloadProfileType
    tags: tags
  }
}

module runtime 'modules/runtime.bicep' = if (deployRuntime) {
  name: 'runtime-${name}'
  params: {
    name: name
    location: location
    environmentId: containerEnvironment.outputs.id
    workloadProfileName: containerEnvironment.outputs.workloadProfileName
    images: {
      registry: platform.outputs.registryLoginServer
      api: '${platform.outputs.registryLoginServer}/cohors-api@sha256:${apiImageDigest}'
      agents: '${platform.outputs.registryLoginServer}/cohors-agents@sha256:${agentsImageDigest}'
    }
    identities: {
      api: { id: identities[0].id, clientId: identities[0].properties.clientId }
      agents: { id: identities[1].id, clientId: identities[1].properties.clientId }
      workflows: { id: identities[2].id, clientId: identities[2].properties.clientId }
      operations: { id: identities[3].id, clientId: identities[3].properties.clientId }
    }
    entra: {
      tenantId: entraTenantId
      apiAudience: apiAudience
      agentsAudience: agentsAudience
      spaClientId: spaClientId
      delegatedScope: spaDelegatedScope
      workflowScope: workflowApiScope
      agentScope: agentsApiScope
    }
    productGrants: productGrants
    agentProducts: agentProducts
    sqlConnection: 'Server=tcp:${sql.outputs.serverFqdn},1433;Database=${sql.outputs.databaseName};Encrypt=true;TrustServerCertificate=false;'
    evidenceContainerUri: '${evidence.outputs.blobEndpoint}evidence'
    search: {
      endpoint: ai.outputs.searchEndpoint
      index: searchIndexName
    }
    model: {
      endpoint: ai.outputs.openAiEndpoint
      deployment: modelDeploymentName
      version: modelVersion
      boundaryReview: foundryBoundaryReview
    }
    network: {
      workflowsSubnetId: networkData.workflowsSubnetId
      operationsSubnetId: networkData.operationsSubnetId
      endpointSubnetId: networkData.endpointsSubnetId
      functionsDnsZoneId: networkData.dns.functions
    }
    functionStorage: {
      workflowsAccount: workflowStorage.outputs.name
      operationsAccount: operationsStorage.outputs.name
      workflowsPackageUri: '${packages.outputs.blobEndpoint}workflows-package'
      operationsPackageUri: '${packages.outputs.blobEndpoint}operations-package'
    }
    functionRuntimeVersion: functionRuntimeVersion
    operationsFunctionName: operationsFunctionName
    insightsName: monitoring.outputs.insightsName
    tags: tags
  }
}

output platformResourceIds object = {
  vnet: networkData.id
  registry: platform.outputs.registryId
  vault: platform.outputs.vaultId
  sqlServer: sql.outputs.serverId
  sqlDatabase: sql.outputs.databaseId
  search: ai.outputs.searchId
  foundry: ai.outputs.foundryId
  evidence: evidence.outputs.id
  ledgerDigests: digests.outputs.id
  applicationInsights: monitoring.outputs.insightsId
  watchdogAlert: monitoring.outputs.?watchdogAlertId
  workspace: monitoring.outputs.workspaceId
  packages: packages.outputs.id
  containerEnvironment: containerEnvironment.outputs.id
  actionGroup: createOwnedAccess ? ownedActionGroup.id : actionGroupResourceId
  egressRouteTable: networkData.routeTableId
}

output ownedAccessSubnets object = networkData.accessSubnets
output workloadName string = name
output sqlDatabaseName string = sql.outputs.databaseName

output workloadIdentities array = [for (role, i) in identityNames: {
  role: role
  resourceId: identities[i].id
  clientId: identities[i].properties.clientId
  objectId: identities[i].properties.principalId
}]

output integrationEndpoints object = {
  registry: platform.outputs.registryLoginServer
  foundryOpenAi: ai.outputs.openAiEndpoint
  evidenceContainer: '${evidence.outputs.blobEndpoint}evidence'
  search: ai.outputs.searchEndpoint
  sqlServer: sql.outputs.serverFqdn
  workflowsPackageContainer: '${packages.outputs.blobEndpoint}workflows-package'
  operationsPackageContainer: '${packages.outputs.blobEndpoint}operations-package'
}

output runtimeUrls object = deployRuntime ? {
  api: runtime!.outputs.apiUrl
  agents: runtime!.outputs.agentsUrl
  workflows: runtime!.outputs.workflowsUrl
  operations: runtime!.outputs.operationsUrl
} : {}
output runtimeResources object = deployRuntime ? runtime!.outputs.resources : {}
