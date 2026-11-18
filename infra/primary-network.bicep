targetScope = 'subscription'

@description('Immutable reviewed start profile; names and CIDRs are generated/selected, not existing resource IDs.')
param profile object
@description('New generated public SSH key, never a retained operator credential.')
param sshPublicKey string
@secure()
@description('Ephemeral generated Windows setup password. It is not stored in the manifest or journal.')
param windowsBootstrapPassword string = ''

var tags = {
  workload: 'cohors'
  environment: 'synthetic-pilot'
  dataClassification: 'synthetic'
  cohorsBinding: profile.bindingHash
  cohorsOwner: profile.deploymentId
  expiresAtUtc: profile.expiresAtUtc
}

resource workload 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: profile.names.workloadResourceGroup
  location: profile.location
  tags: tags
}
resource access 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: profile.names.accessResourceGroup
  location: profile.location
  tags: tags
}

var suffix = take(uniqueString(subscription().subscriptionId, workload.id, profile.namePrefix), 8)
var workloadName = '${profile.namePrefix}-${suffix}'
var compactName = '${profile.namePrefix}${suffix}'

module network 'modules/network.bicep' = {
  name: '${profile.namePrefix}-network'
  scope: workload
  params: {
    name: workloadName
    location: profile.location
    createOwnedAccess: true
    addressPrefix: profile.network.vnet
    containerAppsPrefix: profile.network.containerApps
    workflowsPrefix: profile.network.workflows
    operationsPrefix: profile.network.operations
    endpointsPrefix: profile.network.privateEndpoints
    egressPrefix: profile.network.egress
    operatorPrefix: profile.network.operator
    clientPrefix: profile.network.client
    bastionPrefix: profile.network.bastion
    nvaPrivateIp: profile.network.nvaPrivateIp
    tags: tags
  }
}

module privateAccess 'modules/primary-access.bicep' = {
  name: '${profile.namePrefix}-access'
  scope: access
  params: {
    name: profile.namePrefix
    location: profile.location
    subnets: network.outputs.accessSubnets
    nvaPrivateIp: profile.network.nvaPrivateIp
    linuxImage: profile.access.linuxImage
    linuxVmSize: profile.access.linuxVmSize
    sshPublicKey: sshPublicKey
    includeInteractiveClient: profile.access.includeInteractiveClient
    windowsImage: profile.access.windowsImage
    windowsVmSize: profile.access.windowsVmSize
    windowsBootstrapPassword: windowsBootstrapPassword
    clientOperatorObjectIds: profile.access.clientOperatorObjectIds
    tags: tags
  }
}

output network object = {
  id: network.outputs.id
  endpointsSubnetId: network.outputs.endpointsSubnetId
  containerAppsSubnetId: network.outputs.containerAppsSubnetId
  workflowsSubnetId: network.outputs.workflowsSubnetId
  operationsSubnetId: network.outputs.operationsSubnetId
  routeTableId: network.outputs.routeTableId
  accessSubnets: network.outputs.accessSubnets
  dns: network.outputs.dns
}
output access object = {
  operatorVmId: privateAccess.outputs.operatorVmId
  filterVmId: privateAccess.outputs.filterVmId
  clientVmId: privateAccess.outputs.?clientVmId
  bastionId: privateAccess.outputs.?bastionId
  clientReaderScopes: privateAccess.outputs.clientReaderScopes
  identities: privateAccess.outputs.identities
}
output ownedResourceGroups array = [workload.id, access.id]
output naming object = {
  workloadName: workloadName
  compactName: compactName
}
