@description('Function App resource name.')
param name string

@description('Approved EU candidate region with verified Flex/.NET 10 availability.')
param location string

@description('Dedicated Flex subnet resource ID, not the ACA or endpoint subnet.')
param subnetId string

@description('Private endpoint subnet resource ID.')
param endpointSubnetId string

@description('privatelink.azurewebsites.net resource ID.')
param privateDnsZoneId string

@description('User-assigned identity resource ID with host/package storage data roles.')
param identityResourceId string

@description('User-assigned identity client ID.')
param identityClientId string

@description('Dedicated host account name; no Azure Files connection and no account key.')
param hostStorageAccountName string

@description('Private deployment package blob container URL. Upload the dotnet-publish zip using a separate approved OneDeploy release.')
param packageContainerUrl string

@description('Runtime version accepted by the regional Flex runtime catalog. 10.0 is a candidate until verified.')
@allowed(['10.0'])
param runtimeVersion string

@description('Fixed Flex instance memory candidate.')
@allowed([512, 2048, 4096])
param instanceMemoryMb int = 2048

@description('Flex per-scaling-group ceiling; the provider minimum is 40, not a total-instance/cost cap.')
@minValue(40)
@maxValue(1000)
param maximumInstanceCount int = 40

@description('Verified scaling groups (for example durable/http or function:<actual timer name>). Empty means no always-ready instances.')
param alwaysReady array = []

@description('Host-specific non-secret settings. Fixed security/host settings cannot be overridden.')
param settings object

@description('Non-sensitive cost and ownership tags.')
param tags object

resource plan 'Microsoft.Web/serverfarms@2024-11-01' = {
  name: 'asp-${name}'
  location: location
  tags: tags
  kind: 'functionapp'
  sku: {
    name: 'FC1'
    tier: 'FlexConsumption'
  }
  properties: {
    reserved: true
  }
}

var fixedSettings = {
  AZURE_FUNCTIONS_ENVIRONMENT: 'Production'
  AzureWebJobsStorage__accountName: hostStorageAccountName
  AzureWebJobsStorage__credential: 'managedidentity'
  AzureWebJobsStorage__clientId: identityClientId
  AZURE_CLIENT_ID: identityClientId
  Cohors__LocalOperations: 'false'
  Cohors__AutoMigrate: 'false'
  Cohors__Seed: 'false'
  Cohors__PublicSources: 'false'
}

resource app 'Microsoft.Web/sites@2024-11-01' = {
  name: name
  location: location
  tags: tags
  kind: 'functionapp,linux'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identityResourceId}': {}
    }
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    publicNetworkAccess: 'Disabled'
    virtualNetworkSubnetId: subnetId
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: packageContainerUrl
          authentication: {
            type: 'UserAssignedIdentity'
            userAssignedIdentityResourceId: identityResourceId
          }
        }
      }
      runtime: {
        name: 'dotnet-isolated'
        version: runtimeVersion
      }
      scaleAndConcurrency: {
        instanceMemoryMB: instanceMemoryMb
        maximumInstanceCount: maximumInstanceCount
        alwaysReady: alwaysReady
      }
    }
    siteConfig: {
      minTlsVersion: '1.2'
      scmMinTlsVersion: '1.2'
      http20Enabled: true
      remoteDebuggingEnabled: false
      appSettings: [for setting in items(union(settings, fixedSettings)): {
        name: setting.key
        value: string(setting.value)
      }]
    }
  }
}

resource scmBasicAuth 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2024-11-01' = {
  parent: app
  name: 'scm'
  properties: { allow: false }
}

resource ftpBasicAuth 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2024-11-01' = {
  parent: app
  name: 'ftp'
  properties: { allow: false }
}

module endpoint 'private-endpoint.bicep' = {
  name: 'pe-${name}'
  params: {
    name: 'pe-${name}'
    location: location
    subnetId: endpointSubnetId
    targetResourceId: app.id
    groupId: 'sites'
    privateDnsZoneIds: [privateDnsZoneId]
    tags: tags
  }
}

output id string = app.id
output url string = 'https://${app.properties.defaultHostName}'
