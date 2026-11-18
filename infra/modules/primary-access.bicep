@description('Unique primary-bootstrap prefix, never an existing shared environment.')
param name string
@description('Reviewed EU candidate region.')
param location string
@description('Subnet IDs produced by this bootstrap, not operator inputs.')
param subnets object
@description('Own transparent-filter IPv4 address.')
param nvaPrivateIp string
@description('Explicit immutable Canonical image reference, including version.')
param linuxImage object
@description('Reviewed regional VM SKU. Availability and vCPU quota remain live gates.')
param linuxVmSize string
@description('New generated public SSH key; its private half is not retained or used for operator access.')
param sshPublicKey string
@description('Non-personal local setup account, disabled after Entra client provisioning.')
param linuxAdminUsername string = 'cohorsctl'
@description('Non-personal local Windows setup account, never a business actor.')
param windowsAdminUsername string = 'cohorssetup'
@description('Include only after explicit Preview, licensing, actor, regional and cost reviews.')
param includeInteractiveClient bool = false
@description('Pinned Windows 11 image reference. No latest version.')
param windowsImage object = {}
@description('Reviewed private test-client VM SKU.')
param windowsVmSize string = 'Standard_D2s_v5'
@secure()
@description('One-time generated local VM bootstrap password; never logged, persisted or reused as an Entra credential.')
param windowsBootstrapPassword string = ''
@description('Real, approved test operator object IDs. Owner is not a VM-login grant.')
param clientOperatorObjectIds string[] = []
@description('Immutable ownership, expiry and classification tags.')
param tags object

resource bootstrapIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: 'id-${name}-bootstrap'
  location: location
  tags: tags
}
resource publisherIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: 'id-${name}-publisher'
  location: location
  tags: tags
}
resource deadlineIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: 'id-${name}-deadline'
  location: location
  tags: tags
}

resource filterPublicIp 'Microsoft.Network/publicIPAddresses@2024-07-01' = {
  name: 'pip-${name}-filter-egress'
  location: location
  tags: tags
  sku: { name: 'Standard' }
  properties: { publicIPAllocationMethod: 'Static' }
}

var machines = [
  { role: 'filter', subnet: subnets.egress, size: linuxVmSize }
  { role: 'operator', subnet: subnets.operator, size: linuxVmSize }
]

resource nics 'Microsoft.Network/networkInterfaces@2024-07-01' = [for machine in machines: {
  name: 'nic-${name}-${machine.role}'
  location: location
  tags: tags
  properties: {
    enableIPForwarding: machine.role == 'filter'
    enableAcceleratedNetworking: false
    dnsSettings: machine.role == 'filter' ? { dnsServers: ['168.63.129.16'] } : {}
    ipConfigurations: [
      {
        name: 'private'
        properties: union({
          subnet: { id: machine.subnet }
          privateIPAllocationMethod: machine.role == 'filter' ? 'Static' : 'Dynamic'
          privateIPAddressVersion: 'IPv4'
        }, machine.role == 'filter' ? {
          privateIPAddress: nvaPrivateIp
          publicIPAddress: { id: filterPublicIp.id }
        } : {})
      }
    ]
  }
}]

resource vms 'Microsoft.Compute/virtualMachines@2024-11-01' = [for (machine, i) in machines: {
  name: 'vm-${name}-${machine.role}'
  location: location
  tags: tags
  identity: machine.role == 'operator' ? {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${bootstrapIdentity.id}': {}
      '${publisherIdentity.id}': {}
      '${deadlineIdentity.id}': {}
    }
  } : { type: 'SystemAssigned' }
  properties: {
    hardwareProfile: { vmSize: machine.size }
    securityProfile: {
      securityType: 'TrustedLaunch'
      uefiSettings: { secureBootEnabled: true, vTpmEnabled: true }
      encryptionAtHost: true
    }
    storageProfile: {
      imageReference: linuxImage
      osDisk: {
        createOption: 'FromImage'
        deleteOption: 'Delete'
        managedDisk: { storageAccountType: 'StandardSSD_LRS' }
      }
    }
    osProfile: {
      computerName: '${machine.role}-${take(name, 8)}'
      adminUsername: linuxAdminUsername
      customData: base64(loadTextContent('../bootstrap/linux-foundation.sh'))
      linuxConfiguration: {
        disablePasswordAuthentication: true
        provisionVMAgent: true
        patchSettings: {
          patchMode: 'ImageDefault'
          assessmentMode: 'ImageDefault'
        }
        ssh: {
          publicKeys: [
            { path: '/home/${linuxAdminUsername}/.ssh/authorized_keys', keyData: sshPublicKey }
          ]
        }
      }
    }
    networkProfile: {
      networkInterfaces: [{ id: nics[i].id, properties: { deleteOption: 'Delete' } }]
    }
    diagnosticsProfile: { bootDiagnostics: { enabled: true } }
  }
}]

resource bastionPublicIp 'Microsoft.Network/publicIPAddresses@2024-07-01' = if (includeInteractiveClient) {
  name: 'pip-${name}-bastion'
  location: location
  tags: tags
  sku: { name: 'Standard' }
  properties: { publicIPAllocationMethod: 'Static' }
}

resource bastion 'Microsoft.Network/bastionHosts@2024-05-01' = if (includeInteractiveClient) {
  name: 'bas-${name}'
  location: location
  tags: tags
  sku: { name: 'Basic' }
  properties: {
    scaleUnits: 2
    enableTunneling: false
    enableIpConnect: false
    enableFileCopy: false
    disableCopyPaste: true
    ipConfigurations: [
      {
        name: 'portal'
        properties: {
          subnet: { id: subnets.bastion }
          publicIPAddress: { id: bastionPublicIp.id }
        }
      }
    ]
  }
}

resource clientNic 'Microsoft.Network/networkInterfaces@2024-07-01' = if (includeInteractiveClient) {
  name: 'nic-${name}-test-client'
  location: location
  tags: tags
  properties: {
    ipConfigurations: [
      {
        name: 'private'
        properties: {
          subnet: { id: subnets.client }
          privateIPAllocationMethod: 'Dynamic'
          privateIPAddressVersion: 'IPv4'
        }
      }
    ]
  }
}

resource client 'Microsoft.Compute/virtualMachines@2024-11-01' = if (includeInteractiveClient) {
  name: 'vm-${name}-test-client'
  location: location
  tags: tags
  identity: { type: 'SystemAssigned' }
  properties: {
    licenseType: 'Windows_Client'
    hardwareProfile: { vmSize: windowsVmSize }
    securityProfile: {
      securityType: 'TrustedLaunch'
      encryptionAtHost: true
      uefiSettings: { secureBootEnabled: true, vTpmEnabled: true }
    }
    storageProfile: {
      imageReference: windowsImage
      osDisk: {
        createOption: 'FromImage'
        deleteOption: 'Delete'
        managedDisk: { storageAccountType: 'StandardSSD_LRS' }
      }
    }
    osProfile: {
      computerName: 'cohors-test'
      adminUsername: windowsAdminUsername
      adminPassword: windowsBootstrapPassword
      windowsConfiguration: {
        enableAutomaticUpdates: true
        provisionVMAgent: true
        patchSettings: { assessmentMode: 'ImageDefault', patchMode: 'AutomaticByOS' }
      }
    }
    networkProfile: {
      networkInterfaces: [{ id: clientNic.id, properties: { deleteOption: 'Delete' } }]
    }
    diagnosticsProfile: { bootDiagnostics: { enabled: true } }
  }
}

resource clientEntra 'Microsoft.Compute/virtualMachines/extensions@2024-11-01' = if (includeInteractiveClient) {
  parent: client
  name: 'AADLoginForWindows'
  location: location
  tags: tags
  properties: {
    publisher: 'Microsoft.Azure.ActiveDirectory'
    type: 'AADLoginForWindows'
    typeHandlerVersion: '1.0'
    autoUpgradeMinorVersion: true
    enableAutomaticUpgrade: true
  }
}

resource clientLogins 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for principalId in clientOperatorObjectIds: if (includeInteractiveClient) {
  name: guid(client.id, principalId, 'Virtual Machine User Login')
  scope: client
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'fb879df8-f326-4884-b1cf-06f3ad86be52')
    principalId: principalId
    principalType: 'User'
  }
}]

var readerScopes = includeInteractiveClient ? [client.id, clientNic.id, bastion.id] : []

output operatorVmId string = vms[1].id
output filterVmId string = vms[0].id
output clientVmId string? = includeInteractiveClient ? client.id : null
output bastionId string? = includeInteractiveClient ? bastion.id : null
output clientReaderScopes array = readerScopes
output identities object = {
  bootstrap: { resourceId: bootstrapIdentity.id, clientId: bootstrapIdentity.properties.clientId, objectId: bootstrapIdentity.properties.principalId }
  publisher: { resourceId: publisherIdentity.id, clientId: publisherIdentity.properties.clientId, objectId: publisherIdentity.properties.principalId }
  deadline: { resourceId: deadlineIdentity.id, clientId: deadlineIdentity.properties.clientId, objectId: deadlineIdentity.properties.principalId }
}
