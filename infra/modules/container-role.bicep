@description('Existing storage account in this deployment resource group.')
param accountName string

@description('Existing container receiving the scoped role, never the whole evidence account.')
param containerName string

@description('Managed identity object ID, not client ID.')
param principalId string

@description('Built-in data-role definition GUID.')
param roleDefinitionGuid string

resource account 'Microsoft.Storage/storageAccounts@2025-01-01' existing = {
  name: accountName
}

resource blob 'Microsoft.Storage/storageAccounts/blobServices@2025-01-01' existing = {
  parent: account
  name: 'default'
}

resource container 'Microsoft.Storage/storageAccounts/blobServices/containers@2025-01-01' existing = {
  parent: blob
  name: containerName
}

resource assignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(container.id, principalId, roleDefinitionGuid)
  scope: container
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleDefinitionGuid)
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}
