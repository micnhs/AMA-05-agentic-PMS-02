@description('Short workload name with deterministic suffix.')
param name string

@description('Approved EU candidate region for stored telemetry.')
param location string

@description('Explicit operational telemetry retention, not business evidence retention.')
@minValue(30)
@maxValue(90)
param retentionDays int = 30

@description('Managed identities that may publish telemetry using Entra, not instrumentation-key authorization.')
param telemetryPrincipalIds string[]

@description('Read-only log-alert identity resource ID.')
param alertIdentityResourceId string

@description('Read-only log-alert identity object ID.')
param alertPrincipalId string

@description('Existing approved action group resource ID. Test delivery before operational acceptance.')
param actionGroupResourceId string

@description('Enable the missing-success alert only after the host contract is bound; no claim of live notification delivery.')
param enableWatchdogAlert bool = false

@description('Operations telemetry cloud role name, verified with the application emitter.')
param operationsRoleName string = 'cohors-operations'

@description('Private endpoint subnet resource ID.')
param endpointSubnetId string

@description('All five AMPLS private DNS zone IDs, including the shared blob zone.')
param privateDnsZoneIds string[]

@description('Non-sensitive cost and ownership tags.')
param tags object

resource workspace 'Microsoft.OperationalInsights/workspaces@2025-02-01' = {
  name: 'log-${name}'
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: retentionDays
    publicNetworkAccessForIngestion: 'Disabled'
    publicNetworkAccessForQuery: 'Disabled'
    features: {
      disableLocalAuth: true
      enableLogAccessUsingOnlyResourcePermissions: true
    }
  }
}

resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-${name}'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id
    DisableLocalAuth: true
    DisableIpMasking: false
    SamplingPercentage: 100
    publicNetworkAccessForIngestion: 'Disabled'
    publicNetworkAccessForQuery: 'Disabled'
  }
}

resource publishers 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for principalId in telemetryPrincipalIds: {
  name: guid(insights.id, principalId, 'Monitoring Metrics Publisher')
  scope: insights
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '3913510d-42f4-4e42-8a64-420c390055eb')
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}]

resource alertReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(workspace.id, alertPrincipalId, 'Log Analytics Reader')
  scope: workspace
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '73c42c96-874c-492b-b04d-ab87d138a893')
    principalId: alertPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource scope 'Microsoft.Insights/privateLinkScopes@2021-09-01' = {
  name: 'ampls-${name}'
  location: 'global'
  tags: tags
  properties: {
    accessModeSettings: {
      ingestionAccessMode: 'PrivateOnly'
      queryAccessMode: 'PrivateOnly'
      exclusions: []
    }
  }
}

resource workspaceLink 'Microsoft.Insights/privateLinkScopes/scopedResources@2021-09-01' = {
  parent: scope
  name: 'workspace'
  properties: {
    linkedResourceId: workspace.id
  }
}

resource insightsLink 'Microsoft.Insights/privateLinkScopes/scopedResources@2021-09-01' = {
  parent: scope
  name: 'insights'
  properties: {
    linkedResourceId: insights.id
  }
}

module endpoint 'private-endpoint.bicep' = {
  name: 'pe-ampls-${name}'
  params: {
    name: 'pe-ampls-${name}'
    location: location
    subnetId: endpointSubnetId
    targetResourceId: scope.id
    groupId: 'azuremonitor'
    privateDnsZoneIds: privateDnsZoneIds
    tags: tags
  }
  dependsOn: [workspaceLink, insightsLink]
}

resource missingWatchdog 'Microsoft.Insights/scheduledQueryRules@2023-12-01' = {
  name: 'alert-${name}-watchdog-missing'
  location: location
  tags: tags
  kind: 'LogAlert'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${alertIdentityResourceId}': {}
    }
  }
  properties: {
    description: 'No successful SQL deadline watchdog completion in the last 10 minutes, or telemetry missing. Synthetic pilot; investigate immediately.'
    displayName: 'Cohors watchdog success missing'
    enabled: enableWatchdogAlert
    severity: 1
    evaluationFrequency: 'PT1M'
    windowSize: 'PT15M'
    scopes: [workspace.id]
    autoMitigate: true
    skipQueryValidation: false
    criteria: {
      allOf: [
        {
          query: replace(loadTextContent('../monitoring/watchdog-missing.kql'), '__OPERATIONS_ROLE__', operationsRoleName)
          timeAggregation: 'Maximum'
          metricMeasureColumn: 'MissingSuccess'
          operator: 'GreaterThan'
          threshold: 0
          failingPeriods: {
            numberOfEvaluationPeriods: 1
            minFailingPeriodsToAlert: 1
          }
        }
      ]
    }
    actions: {
      actionGroups: [actionGroupResourceId]
    }
  }
  dependsOn: [alertReader]
}

output workspaceId string = workspace.id
output insightsId string = insights.id
output insightsName string = insights.name
output watchdogAlertId string = missingWatchdog.id
