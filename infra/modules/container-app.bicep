@description('Container App resource name, at most 31 characters.')
@maxLength(31)
param name string

@description('Approved EU candidate region.')
param location string

@description('Internal workload-profiles environment resource ID.')
param environmentId string

@description('Actual explicitly selected environment profile; omission is not proof of Dedicated hosting.')
@allowed(['Consumption', 'D4'])
param workloadProfileName string = 'Consumption'

@description('Full ACR image reference with immutable sha256 digest; tags are rejected by the deployment gate.')
param image string

@description('ACR login server without a scheme.')
param registryServer string

@description('Only this host user-assigned managed identity.')
param identityResourceId string

@description('False confines AgentHost ingress to this environment. True exposes API only to the private VNet ingress.')
param accessibleFromVnet bool

@description('Explicit non-secret environment variables. Credentials and connection-string authentication fields are forbidden.')
param settings object

@description('Pilot floor; a single replica does not establish HA.')
@minValue(1)
@maxValue(3)
param minReplicas int = 1

@description('Per-app scaling ceiling, not a monthly cost cap.')
@minValue(1)
@maxValue(10)
param maxReplicas int = 3

@description('Non-sensitive cost and ownership tags.')
param tags object

resource app 'Microsoft.App/containerApps@2025-07-01' = {
  name: name
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identityResourceId}': {}
    }
  }
  properties: {
    environmentId: environmentId
    workloadProfileName: workloadProfileName
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: accessibleFromVnet
        targetPort: 8080
        transport: 'http'
        allowInsecure: false
      }
      registries: [
        {
          server: registryServer
          identity: identityResourceId
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'host'
          image: image
          env: [for setting in items(settings): {
            name: setting.key
            value: string(setting.value)
          }]
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          probes: [
            {
              type: 'Startup'
              httpGet: { path: '/health/live', port: 8080, scheme: 'HTTP' }
              initialDelaySeconds: 5
              periodSeconds: 5
              timeoutSeconds: 3
              failureThreshold: 30
            }
            {
              type: 'Liveness'
              httpGet: { path: '/health/live', port: 8080, scheme: 'HTTP' }
              periodSeconds: 30
              timeoutSeconds: 3
              failureThreshold: 3
            }
            {
              type: 'Readiness'
              httpGet: { path: '/health/ready', port: 8080, scheme: 'HTTP' }
              periodSeconds: 10
              timeoutSeconds: 5
              failureThreshold: 3
            }
          ]
        }
      ]
      scale: {
        minReplicas: minReplicas
        maxReplicas: maxReplicas
        rules: [
          {
            name: 'http'
            http: {
              metadata: { concurrentRequests: '10' }
            }
          }
        ]
      }
    }
  }
}

output id string = app.id
output url string = 'https://${app.properties.configuration.ingress.fqdn}'
