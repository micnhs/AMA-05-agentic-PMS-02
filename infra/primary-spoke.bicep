targetScope = 'subscription'

@description('New Primary consumer profile; no Firewall/VPN/DNS/WAF lifecycle rights.')
param profile object
@description('Exact fresh owner-issued shared-hub receipt.')
param hubReceipt object
@description('Create the existing private operator only after both peering directions were observed.')
param createOperator bool = false
@description('Generated public operator key.')
param sshPublicKey string
@secure()
@description('Ephemeral Windows credential, never emitted or journalled.')
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

module network 'modules/primary-spoke-network.bicep' = {
  name: '${profile.namePrefix}-spoke'
  scope: workload
  params: {
    profile: profile
    receipt: hubReceipt
    useRemoteGateway: hubReceipt.state != 'reserved'
    tags: tags
  }
}
module privateAccess 'modules/primary-access.bicep' = if (createOperator) {
  name: '${profile.namePrefix}-access'
  scope: access
  params: {
    name: profile.namePrefix
    location: profile.location
    subnets: network.outputs.network.accessSubnets
    includeFilterVm: false
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
output network object = network.outputs.network
output access object = createOperator ? {
  operatorVmId: privateAccess!.outputs.operatorVmId
  clientVmId: privateAccess!.outputs.?clientVmId
  bastionId: privateAccess!.outputs.?bastionId
  clientReaderScopes: privateAccess!.outputs.clientReaderScopes
  identities: privateAccess!.outputs.identities
} : {}
output ownedResourceGroups array = [workload.id, access.id]
output naming object = {
  workloadName: '${profile.namePrefix}-${take(uniqueString(subscription().subscriptionId, workload.id, profile.namePrefix), 8)}'
  compactName: '${profile.namePrefix}${take(uniqueString(subscription().subscriptionId, workload.id, profile.namePrefix), 8)}'
}
