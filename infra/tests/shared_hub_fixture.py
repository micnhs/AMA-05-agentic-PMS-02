"""Synthetic ARM/Graph responses for the real shared-hub collector and receipt emitter.

No addresses, identities, resource availability or deployment are allocated by these fixtures.
"""

import copy
import datetime as dt
from pathlib import Path
import sys
from types import SimpleNamespace
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).parents[1] / "scripts"))
import primary_bootstrap as bootstrap
import shared_hub as hub
import shared_hub_control as control
from primary_control import ARM, GRAPH, digest, file_hash, write_json


NOW = dt.datetime(2026, 11, 19, 8, tzinfo=dt.timezone.utc)
OBSERVED = "2026-11-19T08:00:00Z"


class FixtureClient:
    def __init__(self):
        self.resources, self.collections, self.calls = {}, {}, []

    def add(self, value, version=hub.NETWORK_API):
        self.resources[ARM + value["id"] + "?api-version=" + version] = copy.deepcopy(value)
        return value

    def get(self, url):
        self.calls.append(("GET", url))
        if url not in self.resources:
            raise AssertionError("Unexpected synthetic GET: " + url)
        return copy.deepcopy(self.resources[url])

    def collection(self, url):
        self.calls.append(("LIST", url))
        if url not in self.collections:
            raise AssertionError("Unexpected synthetic LIST: " + url)
        return copy.deepcopy(self.collections[url])

    def request(self, method, url, body=None, *, allowed=(200,), headers=None):
        if method != "GET":
            raise AssertionError("A readback fixture never authorizes a cloud mutation.")
        if url not in self.resources and 404 in allowed:
            self.calls.append(("GET", url))
            return {}, {}, 404
        return self.get(url), {}, 200


def resource(identifier, kind, properties, *, tags=None, location="francecentral", **extra):
    return {"id": identifier, "name": identifier.rsplit("/", 1)[-1], "type": kind, "location": location,
            "etag": 'W/"synthetic-fixture-1"', "tags": tags or {},
            "properties": {"provisioningState": "Succeeded", **properties}, **extra}


def rule(name, direction, access, source, destination, port, priority, protocol="*"):
    return {"name": name, "properties": {"direction": direction, "access": access, "protocol": protocol,
            "sourceAddressPrefix": source, "destinationAddressPrefix": destination,
            "sourcePortRange": "*", "destinationPortRange": port, "priority": priority}}


def synthetic_stage(consumer_key="fabric", state="connected"):
    from test_shared_hub import manifest, consumer

    profile, entry, client = manifest(), consumer(consumer_key), FixtureClient()
    profile["binding"]["tenantId"] = "99999999-8888-4777-8666-555555555555"
    profile["releaseBundle"] = {"infrastructureCommit": "d" * 40}
    entry["state"] = state
    entry["edgePolicy"] = {"dnsDomains": ["login.microsoftonline.com"], "httpsRules": [
        {"name": "identity", "sourceSegments": ["client"], "targetFqdns": ["login.microsoftonline.com"]}]}
    if consumer_key == "primary":
        entry["spoke"]["vnetId"] = entry["spoke"]["resourceGroupId"] + "/providers/Microsoft.Network/virtualNetworks/vnet-primary-spoke"
        for role, subnet in entry["spoke"]["subnets"].items():
            subnet["id"] = entry["spoke"]["vnetId"] + "/subnets/" + hub.PRIMARY_SUBNET_NAMES[role]
    group = hub.hub_group(profile)
    prefix = group + "/providers/Microsoft.Network/"
    vnet_id = prefix + "virtualNetworks/vnet-testhub-hub"
    ownership = {"cohorsBinding": profile["bindingHash"]}
    subnet_names = {"firewall": "AzureFirewallSubnet", "gateway": "GatewaySubnet", "waf": "snet-waf", "endpoints": "snet-hub-endpoints"}
    subnets = {key: {"id": vnet_id + "/subnets/" + name, "addressPrefix": profile["network"][key]}
               for key, name in subnet_names.items()}
    ids = {key: prefix + suffix for key, suffix in {
        "firewallId": "azureFirewalls/afw-testhub-hub", "firewallPolicyId": "firewallPolicies/afwp-testhub-hub",
        "gatewayId": "virtualNetworkGateways/vgw-testhub-hub", "firewallPublicIpId": "publicIPAddresses/pip-testhub-firewall",
        "gatewayPublicIpId": "publicIPAddresses/pip-testhub-vpn", "dnsPolicyId": "dnsResolverPolicies/dnsp-testhub-hub",
        "allowedDomainsId": "dnsResolverDomainLists/dnsdl-testhub-allow",
        "deniedDomainsId": "dnsResolverDomainLists/dnsdl-testhub-deny"}.items()}
    ids.update({key: ids[parent] + suffix for key, parent, suffix in (
        ("allowDnsRuleId", "dnsPolicyId", "/dnsSecurityRules/allow-reviewed-domains"),
        ("denyDnsRuleId", "dnsPolicyId", "/dnsSecurityRules/deny-unmatched"),
        ("dnsLinkId", "dnsPolicyId", "/virtualNetworkLinks/shared-hub"),
        ("privateDataRulesId", "firewallPolicyId", "/ruleCollectionGroups/private-data-no-egress"),
        ("httpsRulesId", "firewallPolicyId", "/ruleCollectionGroups/reviewed-https"),
    )})
    vault_id = group + "/providers/Microsoft.KeyVault/vaults/kv-synthetic-hub"
    vault_endpoint = prefix + "privateEndpoints/pe-testhub-vault"
    identity_id = group + "/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-testhub-waf"
    principal_id = "aaaaaaaa-0000-4000-8000-000000000001"
    zones = [{"id": prefix + "privateDnsZones/" + name, "name": name} for name in profile["dnsZones"]]
    hub_value = {"resourceGroupId": group, "vnetId": vnet_id, "location": profile["location"],
                 "addressPrefix": profile["network"]["vnet"], "subnets": subnets,
                 "firewallId": ids["firewallId"], "firewallPolicyId": ids["firewallPolicyId"], "firewallPrivateIp": "10.80.0.10",
                 "vpnGatewayId": ids["gatewayId"], "dnsPolicyId": ids["dnsPolicyId"], "dnsServerIp": "10.80.0.10",
                 "wafId": None, "wafPrivateIp": None, "ownerDependencies": {
                     "wafIdentityId": identity_id, "wafIdentityPrincipalId": principal_id,
                     "certificateVaultId": vault_id, "certificateVaultUri": "https://kv-synthetic-hub.vault.azure.net/",
                     "certificateEndpointId": vault_endpoint, "certificateEndpointIps": ["10.80.2.10"], "zoneIds": zones}}
    if state == "data-bound" and consumer_key == "fabric":
        endpoint = {"id": entry["spoke"]["resourceGroupId"] + "/providers/Microsoft.Network/privateEndpoints/pe-artifact",
                    "serviceId": entry["spoke"]["resourceGroupId"] + "/providers/Microsoft.Storage/storageAccounts/artifact",
                    "subnetId": entry["spoke"]["subnets"]["operatorEndpoints"]["id"], "groupIds": ["blob"],
                    "fqdns": ["artifact.blob.core.windows.net"]}
        entry["privateEndpoints"] = [endpoint]
        entry["dnsRecords"] = [{"zone": "privatelink.blob.core.windows.net", "name": "artifact", "addresses": ["10.82.3.10"],
                               "sourceResourceId": endpoint["id"], "dnsEvidenceSha256": "e" * 64}]
        entry["operatorEndpoints"] = [{"privateEndpointId": endpoint["id"], "addresses": ["10.82.3.10"],
                                      "fqdns": endpoint["fqdns"], "port": 443}]
        add_endpoint(client, endpoint, "10.82.3.10", control.consumer_owner({"key": consumer_key, **entry}))
    bindings = {"contract": hub.BINDINGS_CONTRACT, "generation": profile["generation"], "revision": 4,
                "previousSha256": "c" * 64, "changedConsumer": consumer_key,
                "consumers": {key: entry if key == consumer_key else None for key in hub.CONSUMERS}}
    selected = hub.hub_policy(profile, bindings, hub_value)
    peers, links, data_records = [], [], []
    active = state not in ("reserved", "offboarded")
    if active:
        for side, local, remote in (("hubToSpoke", vnet_id, entry["spoke"]["vnetId"]),
                                     ("spokeToHub", entry["spoke"]["vnetId"], vnet_id)):
            peer_id = local + "/virtualNetworkPeerings/" + ("to-" + consumer_key if side == "hubToSpoke" else "to-shared-hub")
            client.add(resource(peer_id, "Microsoft.Network/virtualNetworks/virtualNetworkPeerings",
                       {"remoteVirtualNetwork": {"id": remote}, "allowVirtualNetworkAccess": True,
                        "allowForwardedTraffic": side == "spokeToHub", "allowGatewayTransit": side == "hubToSpoke",
                        "useRemoteGateways": side == "spokeToHub" and state != "peered", "peeringState": "Connected"}))
            if side == "hubToSpoke":
                peers.append({"consumer": consumer_key, "id": peer_id})
        client.collections[ARM + entry["spoke"]["vnetId"] + "/virtualNetworkPeerings?api-version=" + hub.NETWORK_API] = [
            client.get(ARM + entry["spoke"]["vnetId"] + "/virtualNetworkPeerings/to-shared-hub?api-version=" + hub.NETWORK_API)]
        add_spoke(client, entry, consumer_key, hub_value, profile["network"]["vpnClientPool"])
    client.collections[ARM + vnet_id + "/virtualNetworkPeerings?api-version=" + hub.NETWORK_API] = [
        client.get(ARM + item["id"] + "?api-version=" + hub.NETWORK_API) for item in peers]
    for zone in zones:
        client.add(resource(zone["id"], "Microsoft.Network/privateDnsZones", {}, tags=ownership, location="global"), hub.DNS_API)
        client.add(resource(zone["id"] + "/virtualNetworkLinks/hub", "Microsoft.Network/privateDnsZones/virtualNetworkLinks",
                            {"registrationEnabled": False, "virtualNetwork": {"id": vnet_id}}, tags=ownership, location="global"), hub.DNS_API)
        if active:
            link_id = zone["id"] + "/virtualNetworkLinks/consumer-" + consumer_key
            client.add(resource(link_id, "Microsoft.Network/privateDnsZones/virtualNetworkLinks",
                                {"registrationEnabled": False, "virtualNetwork": {"id": entry["spoke"]["vnetId"]}},
                                tags=ownership, location="global"), hub.DNS_API)
            links.append({"consumer": consumer_key, "id": link_id})
    for record in entry["dnsRecords"]:
        identifier = prefix + "privateDnsZones/" + record["zone"] + "/A/" + record["name"]
        client.add(resource(identifier, "Microsoft.Network/privateDnsZones/A",
                            {"ttl": 300, "metadata": {"cohorsBinding": profile["bindingHash"], "cohorsConsumer": entry["bindingHash"]},
                             "aRecords": [{"ipv4Address": ip} for ip in record["addresses"]]}, location="global"), hub.DNS_API)
        data_records.append({"consumer": consumer_key, "id": identifier})
    waf_nsg, endpoint_nsg, route_id = prefix + "networkSecurityGroups/nsg-testhub-waf", prefix + "networkSecurityGroups/nsg-testhub-pe", prefix + "routeTables/rt-testhub-waf"
    client.add(resource(waf_nsg, "Microsoft.Network/networkSecurityGroups", {"securityRules": [
        rule("RequiredAzureLoadBalancerHealth", "Inbound", "Allow", "AzureLoadBalancer", "*", "*", 110),
        rule("DenyOtherInbound", "Inbound", "Deny", "*", "*", "*", 4096),
        rule("HubDnsProxyOnly", "Outbound", "Allow", "*", "10.80.0.10", "53", 110),
        rule("ReviewedHttpsViaFirewall", "Outbound", "Allow", "*", "Internet", "443", 120, "Tcp"),
        rule("DenyPlatformDnsBypass", "Outbound", "Deny", "*", "AzurePlatformDNS", "53", 130),
        rule("DenyOtherOutbound", "Outbound", "Deny", "*", "*", "*", 4096),
    ]}, tags=ownership))
    client.add(resource(endpoint_nsg, "Microsoft.Network/networkSecurityGroups", {"securityRules": [
        rule("WafPrivateVaultOnly", "Inbound", "Allow", profile["network"]["waf"], profile["network"]["endpoints"], "443", 100, "Tcp"),
        rule("DenyOtherInbound", "Inbound", "Deny", "*", "*", "*", 4096),
    ]}, tags=ownership))
    client.add(resource(route_id, "Microsoft.Network/routeTables", {"disableBgpRoutePropagation": False, "routes": [
        {"name": "reviewed-public-egress-only", "properties": {"addressPrefix": "0.0.0.0/0", "nextHopType": "VirtualAppliance",
                                                              "nextHopIpAddress": "10.80.0.10"}}]}, tags=ownership))
    actual_subnets = []
    for key, subnet in subnets.items():
        properties = {"addressPrefix": subnet["addressPrefix"], "defaultOutboundAccess": False}
        if key == "waf":
            properties.update(networkSecurityGroup={"id": waf_nsg}, routeTable={"id": route_id},
                              delegations=[{"properties": {"serviceName": "Microsoft.Network/applicationGateways"}}])
        elif key == "endpoints":
            properties.update(networkSecurityGroup={"id": endpoint_nsg}, privateEndpointNetworkPolicies="Enabled")
        actual_subnets.append({"id": subnet["id"], "name": subnet_names[key], "properties": properties})
    client.add(resource(vnet_id, "Microsoft.Network/virtualNetworks", {
        "addressSpace": {"addressPrefixes": [profile["network"]["vnet"]]}, "dhcpOptions": {"dnsServers": ["10.80.0.10"]},
        "subnets": actual_subnets}, tags=ownership))
    client.add(resource(ids["firewallId"], "Microsoft.Network/azureFirewalls", {
        "sku": {"name": "AZFW_VNet", "tier": "Standard"}, "firewallPolicy": {"id": ids["firewallPolicyId"]},
        "ipConfigurations": [{"properties": {"privateIPAddress": "10.80.0.10", "subnet": {"id": subnets["firewall"]["id"]},
                                            "publicIPAddress": {"id": ids["firewallPublicIpId"]}}}]},
        tags=ownership, zones=profile["network"]["zones"]))
    client.add(resource(ids["firewallPolicyId"], "Microsoft.Network/firewallPolicies", {
        "sku": {"tier": "Standard"}, "threatIntelMode": "Deny", "dnsSettings": {"enableProxy": True, "requireProxyForNetworkRules": True}}))
    vpn_identity = {"objectId": "aaaaaaaa-0000-4000-8000-000000000010", "clientId": "aaaaaaaa-0000-4000-8000-000000000011",
                    "servicePrincipalObjectId": "aaaaaaaa-0000-4000-8000-000000000012"}
    client.add(resource(ids["gatewayId"], "Microsoft.Network/virtualNetworkGateways", {
        "gatewayType": "Vpn", "vpnType": "RouteBased", "vpnGatewayGeneration": "Generation1", "enableBgp": False,
        "activeActive": False, "sku": {"name": "VpnGw1AZ", "tier": "VpnGw1AZ"},
        "ipConfigurations": [{"properties": {"subnet": {"id": subnets["gateway"]["id"]}, "publicIPAddress": {"id": ids["gatewayPublicIpId"]}}}],
        "vpnClientConfiguration": {"aadAudience": vpn_identity["clientId"],
            "aadIssuer": "https://sts.windows.net/" + profile["binding"]["tenantId"] + "/",
            "aadTenant": "https://login.microsoftonline.com/" + profile["binding"]["tenantId"],
            "vpnClientProtocols": ["OpenVPN"], "vpnAuthenticationTypes": ["AAD"],
            "vpnClientAddressPool": {"addressPrefixes": [profile["network"]["vpnClientPool"]]}}}, tags=ownership))
    for key in ("firewallPublicIpId", "gatewayPublicIpId"):
        client.add(resource(ids[key], "Microsoft.Network/publicIPAddresses",
                            {"publicIPAllocationMethod": "Static", "publicIPAddressVersion": "IPv4"},
                            tags=ownership, sku={"name": "Standard", "tier": "Regional"}, zones=profile["network"]["zones"]))
    client.add(resource(identity_id, "Microsoft.ManagedIdentity/userAssignedIdentities", {"principalId": principal_id}, tags=ownership), "2024-11-30")
    client.add(resource(vault_id, "Microsoft.KeyVault/vaults", {
        "publicNetworkAccess": "Disabled", "enableRbacAuthorization": True, "enablePurgeProtection": True,
        "enableSoftDelete": True, "softDeleteRetentionInDays": 90, "enabledForDeployment": False,
        "enabledForDiskEncryption": False, "enabledForTemplateDeployment": False,
        "networkAcls": {"bypass": "None", "defaultAction": "Deny", "ipRules": [], "virtualNetworkRules": []},
        "vaultUri": "https://kv-synthetic-hub.vault.azure.net/"}, tags=ownership), "2024-11-01")
    add_endpoint(client, {"id": vault_endpoint, "serviceId": vault_id, "subnetId": subnets["endpoints"]["id"],
                         "groupIds": ["vault"], "fqdns": ["kv-synthetic-hub.vault.azure.net"]}, "10.80.2.10", ownership)
    add_policy(client, ids, profile, vnet_id, selected)
    add_vpn_directory(client, profile, vpn_identity)
    stage = {"hub": hub_value, "edge": {"resources": ids, "firewallPrivateIp": "10.80.0.10"},
             "bindingInput": bindings, "bindings": {"peers": peers, "dnsLinks": links, "dataRecords": data_records,
                                                   "frontendRecords": [], "web": []},
             "metadataReadback": {"observedAtUtc": OBSERVED, "infrastructureCommit": "d" * 40,
                 "compiledTemplateSha256": "e" * 64, "deploymentResourceId":
                 "/subscriptions/" + profile["binding"]["subscriptionId"] + "/providers/Microsoft.Resources/deployments/synthetic-hub-bindings-000004",
                 "resources": [], "trafficAccepted": False, "capacityAccepted": False, "costAccepted": False}}
    engine = SimpleNamespace(manifest=profile, client=client, outputs={"phase-identities": {"vpn": vpn_identity}})
    client.calls.clear()
    return engine, stage


def add_endpoint(client, endpoint, address, tags):
    nic_id = endpoint["id"].replace("/privateEndpoints/", "/networkInterfaces/")
    client.add(resource(endpoint["id"], "Microsoft.Network/privateEndpoints", {
        "subnet": {"id": endpoint["subnetId"]}, "networkInterfaces": [{"id": nic_id}],
        "privateLinkServiceConnections": [{"properties": {"privateLinkServiceId": endpoint["serviceId"],
            "groupIds": endpoint["groupIds"], "privateLinkServiceConnectionState": {"status": "Approved"}}}],
        "customDnsConfigs": [{"fqdn": name, "ipAddresses": [address]} for name in endpoint["fqdns"]]}, tags=tags))
    client.add(resource(nic_id, "Microsoft.Network/networkInterfaces", {"ipConfigurations": [
        {"properties": {"subnet": {"id": endpoint["subnetId"]}, "privateIPAddress": address,
                        "privateLinkConnectionProperties": {"fqdns": endpoint["fqdns"]}}}]}))


def add_spoke(client, entry, key, hub_value, pool):
    tags = control.consumer_owner({"key": key, **entry})
    group, vnet = entry["spoke"]["resourceGroupId"], entry["spoke"]["vnetId"]
    route_id = group + "/providers/Microsoft.Network/routeTables/controlled-egress"
    client.add(resource(route_id, "Microsoft.Network/routeTables", {"disableBgpRoutePropagation": False, "routes": [
        {"name": "controlled-default-egress", "properties": {"addressPrefix": "0.0.0.0/0", "nextHopType": "VirtualAppliance",
                                                             "nextHopIpAddress": hub_value["firewallPrivateIp"]}}]}, tags=tags))
    subnets = []
    for role, subnet in entry["spoke"]["subnets"].items():
        properties = {"addressPrefix": subnet["addressPrefix"]}
        if (key == "fabric" and role in ("client", "agents")) or (key == "primary" and role != "bastion"):
            properties["routeTable"] = {"id": route_id}
        if role != "bastion":
            nsg_id = group + "/providers/Microsoft.Network/networkSecurityGroups/" + role
            rules = [rule("deny-other-in", "Inbound", "Deny", "*", "*", "*", 4096),
                     rule("deny-other-out", "Outbound", "Deny", "*", "*", "*", 4096),
                     rule("dns-only", "Outbound", "Allow", "*", hub_value["firewallPrivateIp"], "53", 100),
                     rule("https-egress", "Outbound", "Allow", "*", "Internet", "443", 110, "Tcp")]
            if role == "operatorEndpoints" and entry["operatorEndpoints"]:
                rules.append(rule("operator-https", "Inbound", "Allow", pool, entry["operatorEndpoints"][0]["addresses"][0], "443", 100, "Tcp"))
            client.add(resource(nsg_id, "Microsoft.Network/networkSecurityGroups", {"securityRules": rules}, tags=tags))
            properties["networkSecurityGroup"] = {"id": nsg_id}
        if role in ("containerApps", "workflows", "operations", "agents"):
            properties["delegations"] = [{"properties": {"serviceName": "Microsoft.App/environments"}}]
        if role in ("privateEndpoints", "endpoints", "operatorEndpoints"):
            properties["privateEndpointNetworkPolicies"] = "Enabled"
        subnets.append({"id": subnet["id"], "name": subnet["id"].rsplit("/", 1)[-1], "properties": properties})
    client.add(resource(vnet, "Microsoft.Network/virtualNetworks", {
        "addressSpace": {"addressPrefixes": [entry["spoke"]["addressPrefix"]]},
        "dhcpOptions": {"dnsServers": [hub_value["firewallPrivateIp"]]}, "subnets": subnets}, tags=tags))


def add_policy(client, ids, profile, vnet_id, selected):
    tags = {"cohorsBinding": profile["bindingHash"]}
    for key, kind, properties in (("dnsPolicyId", "dnsResolverPolicies", {}),
                                  ("allowedDomainsId", "dnsResolverDomainLists", {"domains": selected["dnsDomains"]}),
                                  ("deniedDomainsId", "dnsResolverDomainLists", {"domains": ["."]})):
        client.add(resource(ids[key], "Microsoft.Network/" + kind, properties, tags=tags), hub.POLICY_API)
    dns_rules = []
    for key, priority, action, list_key in (("allowDnsRuleId", 100, "Allow", "allowedDomainsId"),
                                          ("denyDnsRuleId", 65000, "Block", "deniedDomainsId")):
        dns_rules.append(client.add(resource(ids[key], "Microsoft.Network/dnsResolverPolicies/dnsSecurityRules", {
            "dnsSecurityRuleState": "Enabled", "priority": priority, "action": {"actionType": action},
            "dnsResolverDomainLists": [{"id": ids[list_key]}]}, tags=tags), hub.POLICY_API))
    client.collections[ARM + ids["dnsPolicyId"] + "/dnsSecurityRules?api-version=" + hub.POLICY_API] = dns_rules
    link = client.add(resource(ids["dnsLinkId"], "Microsoft.Network/dnsResolverPolicies/virtualNetworkLinks",
                               {"virtualNetwork": {"id": vnet_id}}, tags=tags), hub.POLICY_API)
    client.collections[ARM + ids["dnsPolicyId"] + "/virtualNetworkLinks?api-version=" + hub.POLICY_API] = [link]
    deny = [{"name": "deny-spoke-transit", "priority": 100, "ruleCollectionType": "FirewallPolicyFilterRuleCollection", "action": {"type": "Deny"},
             "rules": [{"name": "no-implicit-private-transit", "ruleType": "NetworkRule", "ipProtocols": ["Any"],
                        "sourceAddresses": selected["sourcePrefixes"], "destinationAddresses": ["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16"], "destinationPorts": ["*"]}]}]
    if selected["privateOnlyFqdns"]:
        deny.append({"name": "deny-private-data-egress", "priority": 110, "ruleCollectionType": "FirewallPolicyFilterRuleCollection", "action": {"type": "Deny"},
                     "rules": [{"name": "private-data-stays-private", "ruleType": "NetworkRule", "ipProtocols": ["TCP"],
                                "sourceAddresses": selected["sourcePrefixes"], "destinationFqdns": selected["privateOnlyFqdns"], "destinationPorts": ["443", "1433"]}]})
    allow = [{"name": "allow-reviewed-https", "priority": 200, "ruleCollectionType": "FirewallPolicyFilterRuleCollection",
              "action": {"type": "Allow"}, "rules": [{**rule, "ruleType": "ApplicationRule", "terminateTLS": False,
                  "protocols": [{"protocolType": "Https", "port": 443}]} for rule in selected["httpsRules"]]}] if selected["httpsRules"] else []
    groups = [client.add(resource(ids[key], "Microsoft.Network/firewallPolicies/ruleCollectionGroups",
                                  {"priority": priority, "ruleCollections": rules}))
              for key, priority, rules in (("privateDataRulesId", 100, deny), ("httpsRulesId", 200, allow))]
    client.collections[ARM + ids["firewallPolicyId"] + "/ruleCollectionGroups?api-version=" + hub.NETWORK_API] = groups


def add_vpn_directory(client, profile, identity):
    app = bootstrap.app_body(profile, "vpn")
    app.update(id=identity["objectId"], appId=identity["clientId"], identifierUris=["api://" + identity["clientId"]],
               passwordCredentials=[], keyCredentials=[])
    client.resources[GRAPH + "/v1.0/applications/" + identity["objectId"] + "?$select=id,appId,signInAudience,identifierUris,api,appRoles,requiredResourceAccess,passwordCredentials,keyCredentials"] = app
    client.resources[GRAPH + "/v1.0/servicePrincipals/" + identity["servicePrincipalObjectId"] + "?$select=id,appId,appOwnerOrganizationId,appRoleAssignmentRequired,accountEnabled"] = {
        "id": identity["servicePrincipalObjectId"], "appId": identity["clientId"], "appOwnerOrganizationId": profile["binding"]["tenantId"],
        "appRoleAssignmentRequired": True, "accountEnabled": True}
    client.collections[GRAPH + "/v1.0/servicePrincipals/" + identity["servicePrincipalObjectId"] + "/appRoleAssignedTo?$select=principalId,principalType,appRoleId,resourceId"] = [
        {"principalId": identifier, "principalType": "User", "appRoleId": "00000000-0000-0000-0000-000000000000",
         "resourceId": identity["servicePrincipalObjectId"]} for identifier in profile["vpnUserObjectIds"]]


def collect(consumer="fabric", state="connected"):
    engine, stage = synthetic_stage(consumer, state)
    observer = control.observe_hub(engine, stage, bound=True, bindings=stage["bindingInput"])
    control.observe_binding_resources(engine, observer, stage)
    with patch.object(control, "timestamp", return_value=OBSERVED), patch.object(hub, "utcnow", return_value=NOW):
        receipt = control.make_receipt(engine, consumer, stage, observer)
    return engine, stage, observer, receipt


def synthetic_waf():
    from test_shared_hub import web

    engine, stage = synthetic_stage("primary", "connected")
    entry = stage["bindingInput"]["consumers"]["primary"]
    entry["state"], entry["web"] = "web-bound", web(entry)
    dependencies = stage["hub"]["ownerDependencies"]
    entry["web"]["certificateSecretId"] = dependencies["certificateVaultUri"] + "secrets/frontend/" + "a" * 32
    gateway_id = hub.hub_group(engine.manifest) + "/providers/Microsoft.Network/applicationGateways/agw-testhub-hub"
    policy_id = hub.hub_group(engine.manifest) + "/providers/Microsoft.Network/ApplicationGatewayWebApplicationFirewallPolicies/primary"
    grant_id = dependencies["certificateVaultId"] + "/secrets/frontend/providers/Microsoft.Authorization/roleAssignments/00000000-0000-4000-8000-000000000033"
    tags = {"cohorsBinding": engine.manifest["bindingHash"]}
    stage["hub"].update(wafId=gateway_id, wafPrivateIp="10.80.1.10")
    stage["bindings"]["web"] = [{"consumer": "primary", "policyId": policy_id, "certificateGrantId": grant_id}]
    spec = entry["web"]
    props = {
        "sku": {"name": "WAF_v2", "tier": "WAF_v2", "capacity": 1}, "enableHttp2": False,
        "globalConfiguration": {"enableRequestBuffering": True, "enableResponseBuffering": True},
        "sslPolicy": {"policyType": "Predefined", "policyName": "AppGwSslPolicy20220101S"},
        "frontendIPConfigurations": [{"name": "private-only", "properties": {"privateIPAllocationMethod": "Static",
            "privateIPAddress": "10.80.1.10", "subnet": {"id": stage["hub"]["subnets"]["waf"]["id"]}}}],
        "frontendPorts": [{"name": "https", "properties": {"port": 443}}],
        "sslCertificates": [{"name": "cert-primary", "properties": {"keyVaultSecretId": spec["certificateSecretId"]}}],
        "httpListeners": [{"name": "https-primary", "properties": {"hostNames": [spec["frontendHost"]], "protocol": "Https",
            "requireServerNameIndication": True, "firewallPolicy": {"id": policy_id}}}],
        "backendAddressPools": [{"name": "backend-primary", "properties": {"backendAddresses": [{"ipAddress": spec["backendPrivateIp"]}]}}],
        "backendHttpSettingsCollection": [{"name": "https-primary", "properties": {
            "protocol": "Https", "port": 443, "hostName": spec["backendFqdn"], "sniName": spec["backendFqdn"],
            "validateSNI": True, "validateCertChainAndExpiry": True, "requestTimeout": 60,
            "connectionDraining": {"enabled": True, "drainTimeoutInSec": 90}, "cookieBasedAffinity": "Disabled",
            "pickHostNameFromBackendAddress": False, "probeEnabled": True, "probe": {"id": gateway_id + "/probes/ready-primary"}}}],
        "probes": [{"name": "ready-primary", "properties": {"host": spec["probe"]["host"], "path": "/health/ready", "port": 443,
            "protocol": "Https", "pickHostNameFromBackendHttpSettings": False, "match": {"statusCodes": ["200"]}}}],
        "requestRoutingRules": [{"name": "route-primary", "properties": {"ruleType": "Basic", "priority": 100,
            "httpListener": {"id": gateway_id + "/httpListeners/https-primary"},
            "backendAddressPool": {"id": gateway_id + "/backendAddressPools/backend-primary"},
            "backendHttpSettings": {"id": gateway_id + "/backendHttpSettingsCollection/https-primary"}}}],
    }
    engine.client.add(resource(gateway_id, "Microsoft.Network/applicationGateways", props, tags=tags,
                               identity={"type": "UserAssigned", "userAssignedIdentities": {dependencies["wafIdentityId"]: {}}}), hub.WAF_API)
    engine.client.add(resource(policy_id, "Microsoft.Network/ApplicationGatewayWebApplicationFirewallPolicies", {
        "policySettings": {"mode": "Prevention", "state": "Enabled", "requestBodyCheck": True, "requestBodyEnforcement": True,
            "fileUploadEnforcement": True, "maxRequestBodySizeInKb": 128, "requestBodyInspectLimitInKB": 128, "fileUploadLimitInMb": 10,
            "logScrubbing": {"state": "Enabled", "scrubbingRules": [
                {"matchVariable": name, "selectorMatchOperator": "EqualsAny", "selector": "*", "state": "Enabled"}
                for name in ("RequestIPAddress", "RequestHeaderNames", "RequestCookieNames", "RequestArgNames",
                             "RequestPostArgNames", "RequestJSONArgNames")]}},
        "managedRules": {"exclusions": [], "managedRuleSets": [{"ruleSetType": "OWASP", "ruleSetVersion": "3.2", "ruleGroupOverrides": []}]},
        "customRules": [{"name": "RejectNonVpnSource", "action": "Block", "priority": 1, "state": "Enabled", "ruleType": "MatchRule",
                        "matchConditions": [{"matchVariables": [{"variableName": "RemoteAddr"}], "operator": "IPMatch",
                                             "negationConditon": True, "matchValues": [engine.manifest["network"]["vpnClientPool"]]}]}],
    }, tags=tags))
    engine.client.add(resource(grant_id, "Microsoft.Authorization/roleAssignments", {
        "principalId": dependencies["wafIdentityPrincipalId"], "principalType": "ServicePrincipal",
        "roleDefinitionId": "/subscriptions/" + engine.manifest["binding"]["subscriptionId"]
            + "/providers/Microsoft.Authorization/roleDefinitions/4633458b-17de-408a-b874-0445c86b69e6"}), "2022-04-01")
    engine.client.collections[ARM + gateway_id + "/providers/Microsoft.Insights/diagnosticSettings?api-version=2021-05-01-preview"] = []
    return engine, stage


def export_fabric(destination):
    destination = Path(destination)
    schema = Path(__file__).parents[1] / "contracts" / "shared-hub-consumer-v1.schema.json"
    cases = []
    for state in ("reserved", "peered", "connected", "data-bound"):
        engine, _, observer, receipt = collect("fabric", state)
        hub.verify_observations(engine.client, receipt)
        arm = {item["id"]: observer.values[item["id"]] for item in receipt["readback"]["resources"]}
        receipt_file, arm_file = destination / (state + ".receipt.json"), destination / (state + ".arm.json")
        write_json(receipt_file, receipt)
        write_json(arm_file, arm)
        cases.append({"state": state, "receiptFile": receipt_file.name, "armFile": arm_file.name,
                      "receiptFileSha256": file_hash(receipt_file), "armFileSha256": file_hash(arm_file),
                      "receiptCanonicalSha256": receipt["receiptSha256"], "armCanonicalSha256": digest(arm)})
    write_json(destination / "fixture-manifest.json", {
        "contract": "cohors-synthetic-hub-fixture-1", "synthetic": True, "cloudObserved": False,
        "schemaSha256": file_hash(schema), "canonicalization": "Python JSON sort_keys=True separators=(',',':') ensure_ascii=True UTF-8",
        "producerSourceSha256": {name: file_hash(Path(__file__).parents[1] / "scripts" / name)
                                for name in ("shared_hub.py", "shared_hub_control.py", "primary_control.py", "primary_bootstrap.py")},
        "fixtureSourceSha256": file_hash(__file__),
        "observedAtUtc": OBSERVED, "trafficAccepted": False, "capacityAccepted": False, "costAccepted": False,
        "cases": cases})
