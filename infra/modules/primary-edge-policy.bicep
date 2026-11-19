@description('Owned primary network name.')
param name string
param location string
param virtualNetworkId string
@description('Link only in the separately reviewed DNS/route-binding stage.')
param bindDnsPolicy bool
@description('Historical default retained; the shared hub uses its own explicit link name.')
param dnsLinkName string = 'own-primary-only'
@description('Shared hub only: deny private transitive traffic before HTTPS application rules.')
param denyTransitivePrivateTraffic bool = false
@description('Reviewed domain list. Azure DNS policy matches descendants too; this is not an exact-QNAME guarantee.')
param dnsDomains string[]
@description('Reviewed HTTPS-only rules: name, exact source subnet prefixes and literal target FQDNs. No default Internet allow.')
param httpsRules array
@description('Actual own data-service names. If routed to egress, deny them before public HTTPS allows; private VNet data paths do not traverse this rule.')
param privateOnlyFqdns string[]
@description('Only the reviewed own source subnets and P2S pool.')
param sourcePrefixes string[]
param tags object

var applicationRules = [for rule in httpsRules: {
  name: rule.name
  ruleType: 'ApplicationRule'
  sourceAddresses: rule.sourceAddresses
  targetFqdns: rule.targetFqdns
  protocols: [{ protocolType: 'Https', port: 443 }]
  terminateTLS: false
}]

resource policy 'Microsoft.Network/firewallPolicies@2024-07-01' = {
  name: 'afwp-${name}'
  location: location
  properties: {
    sku: { tier: 'Standard' }
    threatIntelMode: 'Deny'
    dnsSettings: { enableProxy: true, requireProxyForNetworkRules: true }
  }
}

resource privateData 'Microsoft.Network/firewallPolicies/ruleCollectionGroups@2024-07-01' = {
  parent: policy
  name: 'private-data-no-egress'
  properties: {
    priority: 100
    ruleCollections: concat(denyTransitivePrivateTraffic ? [
      {
        name: 'deny-spoke-transit'
        priority: 100
        ruleCollectionType: 'FirewallPolicyFilterRuleCollection'
        action: { type: 'Deny' }
        rules: [
          {
            name: 'no-implicit-private-transit'
            ruleType: 'NetworkRule'
            ipProtocols: ['Any']
            sourceAddresses: sourcePrefixes
            destinationAddresses: ['10.0.0.0/8', '172.16.0.0/12', '192.168.0.0/16']
            destinationPorts: ['*']
          }
        ]
      }
    ] : [], empty(privateOnlyFqdns) ? [] : [
      {
        name: 'deny-private-data-egress'
        priority: denyTransitivePrivateTraffic ? 110 : 100
        ruleCollectionType: 'FirewallPolicyFilterRuleCollection'
        action: { type: 'Deny' }
        rules: [
          {
            name: 'private-data-stays-private'
            ruleType: 'NetworkRule'
            ipProtocols: ['TCP']
            sourceAddresses: sourcePrefixes
            destinationFqdns: privateOnlyFqdns
            destinationPorts: ['443', '1433']
          }
        ]
      }
    ])
  }
}

resource https 'Microsoft.Network/firewallPolicies/ruleCollectionGroups@2024-07-01' = {
  parent: policy
  name: 'reviewed-https'
  properties: {
    priority: 200
    ruleCollections: empty(httpsRules) ? [] : [
      {
        name: 'allow-reviewed-https'
        priority: 200
        ruleCollectionType: 'FirewallPolicyFilterRuleCollection'
        action: { type: 'Allow' }
        rules: applicationRules
      }
    ]
  }
}

resource dnsPolicy 'Microsoft.Network/dnsResolverPolicies@2025-05-01' = {
  name: 'dnsp-${name}'
  location: location
  tags: tags
  properties: {}
}

resource allowedDomains 'Microsoft.Network/dnsResolverDomainLists@2025-05-01' = if (!empty(dnsDomains)) {
  name: 'dnsdl-${name}-allowed'
  location: location
  tags: tags
  properties: { domains: dnsDomains }
}

resource allDomains 'Microsoft.Network/dnsResolverDomainLists@2025-05-01' = {
  name: 'dnsdl-${name}-deny-rest'
  location: location
  tags: tags
  properties: { domains: ['.'] }
}

resource allowDns 'Microsoft.Network/dnsResolverPolicies/dnsSecurityRules@2025-05-01' = if (!empty(dnsDomains)) {
  parent: dnsPolicy
  name: 'allow-reviewed-domains'
  location: location
  tags: tags
  properties: {
    action: { actionType: 'Allow' }
    dnsResolverDomainLists: [{ id: allowedDomains!.id }]
    dnsSecurityRuleState: 'Enabled'
    priority: 100
  }
}

resource denyDns 'Microsoft.Network/dnsResolverPolicies/dnsSecurityRules@2025-05-01' = {
  parent: dnsPolicy
  name: 'deny-unmatched'
  location: location
  tags: tags
  properties: {
    action: { actionType: 'Block' }
    dnsResolverDomainLists: [{ id: allDomains.id }]
    dnsSecurityRuleState: 'Enabled'
    priority: 65000
  }
}

resource dnsLink 'Microsoft.Network/dnsResolverPolicies/virtualNetworkLinks@2025-05-01' = if (bindDnsPolicy) {
  parent: dnsPolicy
  name: dnsLinkName
  location: location
  tags: tags
  properties: { virtualNetwork: { id: virtualNetworkId } }
  dependsOn: [allowDns, denyDns]
}

output firewallPolicyId string = policy.id
output dnsPolicyId string = dnsPolicy.id
output dnsPolicyLinked bool = bindDnsPolicy
output resources object = {
  dnsPolicyId: dnsPolicy.id
  allowedDomainsId: empty(dnsDomains) ? '' : allowedDomains!.id
  deniedDomainsId: allDomains.id
  allowDnsRuleId: empty(dnsDomains) ? '' : allowDns!.id
  denyDnsRuleId: denyDns.id
  dnsLinkId: bindDnsPolicy ? dnsLink!.id : ''
  privateDataRulesId: privateData.id
  httpsRulesId: https.id
}
