@description('Globally unique SQL logical server name.')
param name string

@description('Database name without clinical or personal identifiers.')
param databaseName string

@description('Approved EU candidate region.')
param location string

@description('Existing privileged Entra administrative group object ID. Not a workload identity.')
param adminGroupObjectId string

@description('Existing Entra administrative group display name.')
param adminGroupName string

@description('Reviewed General Purpose provisioned Gen5 vCore count.')
@allowed([2, 4, 8])
param vCores int

@description('Database size cap in GiB. Storage cost still requires explicit review.')
@minValue(2)
@maxValue(128)
param maxSizeGiB int

@description('Point-in-time restore retention; not evidence retention.')
@minValue(7)
@maxValue(35)
param backupRetentionDays int

@description('In-region backup redundancy, not a geo-replication commitment.')
@allowed(['Local', 'Zone'])
param backupRedundancy string

@description('Enable only after region/SKU/zone support and cost review.')
param zoneRedundant bool

@description('When false, omit the security policy instead of changing existing/inherited protection. Separate activation reviews are required.')
param deployThreatProtection bool = false

@description('Reviewed SecOps recipients; notifications may reference sensitive security events.')
param securityNotificationEmails string[] = []

@description('Private endpoint subnet resource ID.')
param endpointSubnetId string

@description('SQL private DNS zone resource ID.')
param privateDnsZoneId string
@description('Shared-hub consumer mode leaves record registration to the hub owner.')
param managePrivateDns bool = true

@description('Log Analytics workspace resource ID.')
param workspaceId string

@description('Non-sensitive cost and ownership tags.')
param tags object

resource server 'Microsoft.Sql/servers@2023-08-01' = {
  name: name
  location: location
  tags: tags
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    version: '12.0'
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Disabled'
    restrictOutboundNetworkAccess: 'Enabled'
    administrators: {
      administratorType: 'ActiveDirectory'
      principalType: 'Group'
      login: adminGroupName
      sid: adminGroupObjectId
      tenantId: tenant().tenantId
      azureADOnlyAuthentication: true
    }
  }
}

resource entraOnly 'Microsoft.Sql/servers/azureADOnlyAuthentications@2023-08-01' = {
  parent: server
  name: 'Default'
  properties: {
    azureADOnlyAuthentication: true
  }
}

resource connectionPolicy 'Microsoft.Sql/servers/connectionPolicies@2023-08-01' = {
  parent: server
  name: 'default'
  properties: {
    connectionType: 'Proxy'
  }
}

resource threatProtection 'Microsoft.Sql/servers/securityAlertPolicies@2023-08-01' = if (deployThreatProtection) {
  parent: server
  name: 'Default'
  properties: {
    state: 'Enabled'
    disabledAlerts: []
    emailAccountAdmins: false
    emailAddresses: securityNotificationEmails
  }
}

resource database 'Microsoft.Sql/servers/databases@2023-08-01' = {
  parent: server
  name: databaseName
  location: location
  tags: tags
  sku: {
    name: 'GP_Gen5'
    tier: 'GeneralPurpose'
    family: 'Gen5'
    capacity: vCores
  }
  properties: {
    collation: 'SQL_Latin1_General_CP1_CI_AS'
    maxSizeBytes: maxSizeGiB * 1073741824
    zoneRedundant: zoneRedundant
    requestedBackupStorageRedundancy: backupRedundancy
  }
}

resource tde 'Microsoft.Sql/servers/databases/transparentDataEncryption@2023-08-01' = {
  parent: database
  name: 'current'
  properties: {
    state: 'Enabled'
  }
}

resource retention 'Microsoft.Sql/servers/databases/backupShortTermRetentionPolicies@2023-08-01' = {
  parent: database
  name: 'default'
  properties: {
    retentionDays: backupRetentionDays
    diffBackupIntervalInHours: 12
  }
}

resource master 'Microsoft.Sql/servers/databases@2023-08-01' existing = {
  parent: server
  name: 'master'
}

// Even these selected groups can include statement text; approve classification and the sink before deployment.
resource audit 'Microsoft.Sql/servers/auditingSettings@2023-08-01' = {
  parent: server
  name: 'default'
  properties: {
    state: 'Enabled'
    isAzureMonitorTargetEnabled: true
    auditActionsAndGroups: [
      'FAILED_DATABASE_AUTHENTICATION_GROUP'
      'SUCCESSFUL_DATABASE_AUTHENTICATION_GROUP'
      'DATABASE_PRINCIPAL_CHANGE_GROUP'
      'DATABASE_PERMISSION_CHANGE_GROUP'
      'DATABASE_ROLE_MEMBER_CHANGE_GROUP'
      'LEDGER_OPERATION_GROUP'
    ]
  }
}

resource serverAuditLogs 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'server-security-audit'
  scope: master
  properties: {
    workspaceId: workspaceId
    logs: [
      { category: 'SQLSecurityAuditEvents', enabled: true }
    ]
  }
}

resource databaseMetrics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'metrics-only'
  scope: database
  properties: {
    workspaceId: workspaceId
    metrics: [
      { category: 'Basic', enabled: true }
    ]
  }
}

module endpoint 'private-endpoint.bicep' = {
  name: 'pe-${name}'
  params: {
    name: 'pe-${name}'
    location: location
    subnetId: endpointSubnetId
    targetResourceId: server.id
    groupId: 'sqlServer'
    privateDnsZoneIds: [privateDnsZoneId]
    managePrivateDns: managePrivateDns
    tags: tags
  }
}

output serverId string = server.id
output databaseId string = database.id
output serverFqdn string = server.properties.fullyQualifiedDomainName
output databaseName string = database.name
