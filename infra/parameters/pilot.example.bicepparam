using '../main.bicep'

// Compilation example only. None of these identifiers or regional/SKU combinations is verified.
param location = 'westeurope'
param namePrefix = 'cohors'
param egressRouteTableResourceId = '/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/REPLACE-platform-rg/providers/Microsoft.Network/routeTables/REPLACE-reviewed-egress'
param actionGroupResourceId = '/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/REPLACE-monitor-rg/providers/Microsoft.Insights/actionGroups/REPLACE-reviewed-alerting'
param sqlAdminGroupObjectId = '00000000-0000-0000-0000-000000000000'
param sqlAdminGroupName = 'REPLACE-reviewed-sql-admin-group'

param deployRuntime = false
param deployModel = false
param enableWatchdogAlert = false
param enableDigestPublisherGrant = false
param deploySqlThreatProtection = false
param sqlSecurityNotificationEmails = []
