targetScope = 'subscription'

@description('Immutable reviewed start profile; names and CIDRs are generated/selected, not existing resource IDs.')
param profile object
@description('Second stage only: bind the actual Firewall IP and create the private operator after explicit route/DNS review.')
param bindManagedEdge bool = false
@description('Actual preceding stage output, freshly read back by the controller. Never assume a subnet host address.')
param firewallPrivateIp string = ''
@description('Later policy stages do not redeploy the network, gateway or operator credentials.')
param policyOnly bool = false
@description('Exact preceding owned network output, never manually supplied resource IDs.')
param precedingNetwork object = {}
@description('Actual own single-tenant VPN audience with four-user assignment readback.')
param vpnAudienceClientId string
@description('Normalized, source-role-restricted policy from the validated private manifest.')
param edgePolicy object
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

module network 'modules/network.bicep' = if (!policyOnly) {
  name: '${profile.namePrefix}-network'
  scope: workload
  params: {
    name: workloadName
    location: profile.location
    createOwnedAccess: true
    managedEdge: true
    configureCustomDns: bindManagedEdge && !empty(firewallPrivateIp)
    dnsProxyAddress: firewallPrivateIp
    addressPrefix: profile.network.vnet
    containerAppsPrefix: profile.network.containerApps
    workflowsPrefix: profile.network.workflows
    operationsPrefix: profile.network.operations
    endpointsPrefix: profile.network.privateEndpoints
    operatorPrefix: profile.network.operator
    clientPrefix: profile.network.client
    bastionPrefix: profile.network.bastion
    firewallPrefix: profile.network.firewall
    gatewayPrefix: profile.network.gateway
    vpnClientPrefix: profile.network.vpnClientPool
    firewallPrivateIp: bindManagedEdge ? firewallPrivateIp : ''
    tags: tags
  }
}

module policy 'modules/primary-edge-policy.bicep' = {
  name: '${profile.namePrefix}-edge-policy'
  scope: workload
  params: {
    name: workloadName
    location: profile.location
    virtualNetworkId: policyOnly ? precedingNetwork.network.id : network!.outputs.id
    bindDnsPolicy: bindManagedEdge
    dnsDomains: edgePolicy.dnsDomains
    httpsRules: edgePolicy.httpsRules
    privateOnlyFqdns: edgePolicy.privateOnlyFqdns
    sourcePrefixes: edgePolicy.sourcePrefixes
    tags: tags
  }
}

module edge 'modules/primary-edge.bicep' = if (!policyOnly) {
  name: '${profile.namePrefix}-managed-edge'
  scope: workload
  params: {
    name: workloadName
    location: profile.location
    subnets: network!.outputs.accessSubnets
    firewallPolicyId: policy.outputs.firewallPolicyId
    vpnClientPrefix: profile.network.vpnClientPool
    enablePointToSite: bindManagedEdge
    tenantId: profile.binding.tenantId
    vpnAudienceClientId: vpnAudienceClientId
    zones: profile.network.zones
    tags: tags
  }
}

module privateAccess 'modules/primary-access.bicep' = if (bindManagedEdge && !policyOnly) {
  name: '${profile.namePrefix}-access'
  scope: access
  params: {
    name: profile.namePrefix
    location: profile.location
    subnets: network!.outputs.accessSubnets
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
  dependsOn: [edge]
}

output network object = policyOnly ? precedingNetwork.network : {
  id: network!.outputs.id
  endpointsSubnetId: network!.outputs.endpointsSubnetId
  containerAppsSubnetId: network!.outputs.containerAppsSubnetId
  workflowsSubnetId: network!.outputs.workflowsSubnetId
  operationsSubnetId: network!.outputs.operationsSubnetId
  routeTableId: network!.outputs.routeTableId
  gatewayRouteTableId: network!.outputs.gatewayRouteTableId
  managedEdgeBound: network!.outputs.managedEdgeBound
  accessSubnets: network!.outputs.accessSubnets
  dns: network!.outputs.dns
}
output access object = policyOnly ? precedingNetwork.access : bindManagedEdge ? {
  operatorVmId: privateAccess!.outputs.operatorVmId
  clientVmId: privateAccess!.outputs.?clientVmId
  bastionId: privateAccess!.outputs.?bastionId
  clientReaderScopes: privateAccess!.outputs.clientReaderScopes
  identities: privateAccess!.outputs.identities
} : {}
output edge object = policyOnly ? precedingNetwork.edge : {
  resources: union(edge!.outputs.resources, policy.outputs.resources)
  firewallPrivateIp: edge!.outputs.firewallPrivateIp
  dnsPolicyId: policy.outputs.dnsPolicyId
  dnsPolicyLinked: policy.outputs.dnsPolicyLinked
  clientAccess: edge!.outputs.clientAccess
}
output ownedResourceGroups array = [workload.id, access.id]
output naming object = {
  workloadName: workloadName
  compactName: compactName
}
