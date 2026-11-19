targetScope = 'subscription'

@description('Explicit versioned topology; legacy remains inspectable, never implicitly adopted as a shared hub.')
@allowed(['separate-primary-v2', 'shared-hub-v1', 'shared-primary-v3'])
param topology string = 'separate-primary-v2'
@description('Source-bound private manifest projection, not a deployment approval.')
param profile object
@description('Separate readback-bound route/DNS stage.')
param bindManagedEdge bool = false
@description('Actual preceding Firewall IP, never a subnet-address guess.')
param firewallPrivateIp string = ''
@description('Historical v2 policy-only stage.')
param policyOnly bool = false
@description('Actual prior network outputs.')
param precedingNetwork object = {}
@description('Actual owner-created assignment-required audience.')
param vpnAudienceClientId string = ''
@description('Validated source-scoped DNS/HTTPS policy.')
param edgePolicy object = {}
@description('Source-bound public operator key.')
param sshPublicKey string = ''
@secure()
@description('Ephemeral Windows credential; never emitted.')
param windowsBootstrapPassword string = ''
@description('Shared hub stage; bindings are separately reviewed full snapshots.')
@allowed(['network', 'edge-bind', 'hub-bindings'])
param hubStage string = 'network'
@description('Monotonic hub binding snapshot, retaining the other consumer.')
param hubBindings object = {}
@description('Actual hub baseline outputs, not supplied external resource guesses.')
param hubPrevious object = {}
@description('Exact names derived by the controller from the verified hub ARM IDs and reviewed versioned secret URIs.')
param hubResourceNames object = {}
@description('Owner-issued and freshly verified read-only hub receipt for the Primary consumer.')
param hubReceipt object = {}

module legacy 'primary-network-v2.bicep' = if (topology == 'separate-primary-v2') {
  name: '${profile.namePrefix}-legacy-v2'
  params: {
    profile: profile
    bindManagedEdge: bindManagedEdge
    firewallPrivateIp: firewallPrivateIp
    policyOnly: policyOnly
    precedingNetwork: precedingNetwork
    vpnAudienceClientId: vpnAudienceClientId
    edgePolicy: edgePolicy
    sshPublicKey: sshPublicKey
    windowsBootstrapPassword: windowsBootstrapPassword
  }
}

module hub 'shared-hub.bicep' = if (topology == 'shared-hub-v1') {
  name: '${profile.namePrefix}-shared-hub'
  params: {
    profile: profile
    stage: hubStage
    bindings: hubBindings
    previous: hubPrevious
    resourceNames: hubResourceNames
    firewallPrivateIp: firewallPrivateIp
    vpnAudienceClientId: vpnAudienceClientId
    edgePolicy: edgePolicy
  }
}

module spoke 'primary-spoke.bicep' = if (topology == 'shared-primary-v3') {
  name: '${profile.namePrefix}-shared-spoke'
  params: {
    profile: profile
    hubReceipt: hubReceipt
    createOperator: bindManagedEdge
    sshPublicKey: sshPublicKey
    windowsBootstrapPassword: windowsBootstrapPassword
  }
}

output network object = topology == 'separate-primary-v2' ? legacy!.outputs.network : topology == 'shared-primary-v3' ? spoke!.outputs.network : {}
output access object = topology == 'separate-primary-v2' ? legacy!.outputs.access : topology == 'shared-primary-v3' ? spoke!.outputs.access : {}
output edge object = topology == 'separate-primary-v2' ? legacy!.outputs.edge : topology == 'shared-hub-v1' ? hub!.outputs.edge : {}
output hub object = topology == 'shared-hub-v1' ? hub!.outputs.hub : {}
output bindings object = topology == 'shared-hub-v1' ? hub!.outputs.bindings : {}
output ownedResourceGroups array = topology == 'separate-primary-v2' ? legacy!.outputs.ownedResourceGroups : topology == 'shared-hub-v1' ? hub!.outputs.ownedResourceGroups : spoke!.outputs.ownedResourceGroups
output naming object = topology == 'separate-primary-v2' ? legacy!.outputs.naming : topology == 'shared-primary-v3' ? spoke!.outputs.naming : {}
