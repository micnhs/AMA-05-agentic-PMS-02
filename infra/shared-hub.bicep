targetScope = 'subscription'

@description('Independent source-bound hub owner profile; no consumer can invoke its lifecycle.')
param profile object
@description('Explicit phase from the existing journalled bootstrap controller.')
@allowed(['network', 'edge-bind', 'hub-bindings'])
param stage string
@description('Exact preceding successful owned hub outputs.')
param previous object = {}
@description('Resource-name bindings projected from the verified preceding ARM IDs, never free-form adoption inputs.')
param resourceNames object = {}
@description('Actual Firewall address, required before edge binding.')
param firewallPrivateIp string = ''
@description('Actual shared assignment-required audience.')
param vpnAudienceClientId string
@description('Validated complete source-scoped policy, including all retained consumers.')
param edgePolicy object
@description('Reviewed monotonic full consumer snapshot; no implicit removal.')
param bindings object = {}

var tags = {
  workload: 'cohors-shared-network'
  environment: 'synthetic-pilot'
  dataClassification: 'synthetic'
  cohorsBinding: profile.bindingHash
  cohorsOwner: profile.deploymentId
  cohorsGeneration: profile.generation
  expiresAtUtc: profile.expiresAtUtc
}

resource hubGroup 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: profile.names.hubResourceGroup
  location: profile.location
  tags: tags
}

module network 'modules/shared-hub-network.bicep' = if (stage != 'hub-bindings') {
  name: '${profile.namePrefix}-hub-network'
  scope: hubGroup
  params: {
    profile: profile
    bound: stage == 'edge-bind'
    firewallPrivateIp: firewallPrivateIp
    vpnAudienceClientId: vpnAudienceClientId
    edgePolicy: edgePolicy
    tags: tags
  }
}

module consumers 'modules/shared-hub-bindings.bicep' = if (stage == 'hub-bindings') {
  name: '${profile.namePrefix}-hub-bindings-${bindings.revision}'
  scope: hubGroup
  params: {
    profile: profile
    hub: previous.hub
    resourceNames: resourceNames
    bindings: bindings
    edgePolicy: edgePolicy
    tags: tags
  }
}

output hub object = stage == 'hub-bindings' ? union(previous.hub, {
  wafId: consumers.?outputs.wafId
  wafPrivateIp: consumers.?outputs.wafPrivateIp
}) : network!.outputs.hub
output edge object = stage == 'hub-bindings' ? previous.edge : network!.outputs.edge
output bindings object = stage == 'hub-bindings' ? consumers!.outputs.references : {}
output ownedResourceGroups array = [hubGroup.id]
