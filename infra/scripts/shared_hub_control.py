"""Shared-network adapter for the existing Bootstrap, Journal and bound ARM/Graph client."""

import copy
import datetime as dt
from pathlib import Path

from primary_control import ARM, GRAPH, GateError, digest, file_hash, parse_time, read_json, timestamp, utcnow, write_json
from primary_network import verify_vpn_identity, observe_policy
from shared_hub import (HUB_CONTRACT, PRIMARY_CONTRACT, RECEIPT_CONTRACT, BINDINGS_CONTRACT, CONSUMERS,
                        OWNERSHIP, NETWORK_API, WAF_API, DNS_API, POLICY_API, active_consumers,
                        arm_id, hub_group, hub_policy, latest_receipt, private_ip, sha, policy,
                        validate_bindings, validate_receipt, validate_wire_receipt, verify_observations)


def latest_hub_stage(outputs):
    revisions = [value for key, value in outputs.items() if key.startswith("phase-hub-bindings-")]
    if revisions:
        return max(revisions, key=lambda value: value["bindingInput"]["revision"])
    return outputs.get("phase-edge-bind") or outputs.get("phase-network")


def previous_bindings(outputs):
    stage = latest_hub_stage(outputs)
    return (stage or {}).get("bindingInput")


def empty_bindings(manifest):
    return {"contract": BINDINGS_CONTRACT, "generation": manifest["generation"], "revision": 0,
            "previousSha256": None, "changedConsumer": None, "consumers": dict.fromkeys(CONSUMERS)}


def owned_references(stage, consumer):
    references = (stage or {}).get("bindings", {})
    result = set()
    for key in ("peers", "dnsLinks", "dataRecords", "frontendRecords"):
        result.update(item["id"] for item in references.get(key, []) if item["consumer"] == consumer)
    for item in references.get("web", []):
        if item["consumer"] == consumer:
            result.update(item[key] for key in ("policyId", "certificateGrantId"))
    return result


def prepare_plan(plan, manifest, outputs, binding_input):
    from primary_bootstrap import app_body
    phase = plan["phase"]
    if phase == "identities":
        plan["directoryChanges"] = {"applications": [app_body(manifest, "vpn")],
                                    "resolvedUsersEvidenceSha256": manifest["resolvedUsersEvidenceSha256"],
                                    "vpnUserObjectIds": manifest["vpnUserObjectIds"],
                                    "newUserLookups": False, "businessRolesChanged": False}
    if phase in ("network", "edge-bind", "hub-bindings"):
        plan["template"] = "primary-network.bicep"
    if phase != "hub-bindings":
        if binding_input is not None:
            raise GateError("Consumer snapshots are accepted only in the explicit hub-bindings phase.")
        return
    validate_bindings(binding_input, manifest, previous_bindings(outputs))
    plan["bindingInput"] = binding_input
    plan["operationPhase"] = f"hub-bindings-{binding_input['revision']:06d}"
    old = latest_hub_stage(outputs)
    key = binding_input["changedConsumer"]
    entry = binding_input["consumers"][key]
    plan["offboardReferences"] = sorted(owned_references(old, key)) if entry and entry["state"] == "offboarded" else []
    active_web = [item for item in active_consumers(binding_input) if item["web"]]
    plan["deleteUnreferencedWaf"] = old["hub"].get("wafId") if not active_web and entry and entry["state"] == "offboarded" else None
    plan["sharedResourceImpact"] = "One shared WAF configuration is updated in place; unchanged consumer bindings are retained. Configuration updates can drain active connections."


def validate_plan(plan, manifest, outputs):
    if plan["phase"] != "hub-bindings":
        return
    candidate = {"phase": "hub-bindings"}
    prepare_plan(candidate, manifest, outputs, plan.get("bindingInput"))
    for key in ("operationPhase", "bindingInput", "offboardReferences", "deleteUnreferencedWaf", "sharedResourceImpact"):
        if candidate[key] != plan.get(key):
            raise GateError("The binding revision or targeted release effects differ from the owned preceding snapshot.")


def parameters(manifest, phase, outputs, binding_input):
    previous = latest_hub_stage(outputs) or {}
    selected = binding_input if phase == "hub-bindings" else empty_bindings(manifest)
    names = {}
    if phase == "hub-bindings":
        names = {
            "vnet": arm_id(previous["hub"]["vnetId"], scope=hub_group(manifest), kind="Microsoft.Network/virtualNetworks").rsplit("/", 1)[1],
            "certificateVault": arm_id(previous["hub"]["ownerDependencies"]["certificateVaultId"],
                                       scope=hub_group(manifest), kind="Microsoft.KeyVault/vaults").rsplit("/", 1)[1],
            "certificates": {item["key"]: item["web"]["certificateSecretId"].split("/")[4]
                             for item in active_consumers(selected) if item["web"]},
        }
    profile = {key: manifest[key] for key in ("contract", "bindingHash", "deploymentId", "generation",
                                             "expiresAtUtc", "names", "namePrefix", "location", "network", "waf", "dnsZones")}
    profile["binding"] = {"tenantId": manifest["binding"]["tenantId"]}
    return {key: {"value": value} for key, value in {
        "topology": "shared-hub-v1", "profile": profile, "hubStage": phase,
        "hubBindings": selected, "hubPrevious": previous, "hubResourceNames": names,
        "firewallPrivateIp": previous.get("hub", {}).get("firewallPrivateIp", ""),
        "vpnAudienceClientId": outputs["phase-identities"]["vpn"]["clientId"],
        "edgePolicy": hub_policy(manifest, selected, previous.get("hub")),
    }.items()}


class Observation:
    def __init__(self, client, scopes):
        self.client, self.scopes, self.values, self.versions = client, scopes, {}, {}

    def get(self, identifier, version=NETWORK_API, *, owner=None, kind=None):
        arm_id(identifier, kind=kind)
        if not any(identifier.lower().startswith(scope.lower() + "/") for scope in self.scopes):
            raise GateError("Readback is outside the explicitly bound hub/consumer resource groups.")
        if identifier in self.values and self.versions[identifier] != version:
            raise GateError("One readback cannot mix provider versions for the same resource.")
        value = self.values[identifier] if identifier in self.values else self.client.get(ARM + identifier + "?api-version=" + version)
        if (value.get("id", "").lower() != identifier.lower()
                or kind and value.get("type", "").lower() != kind.lower()):
            raise GateError("The provider returned a different resource identity.")
        expected_tags = owner if isinstance(owner, dict) else {"cohorsBinding": owner}
        if owner and any((value.get("tags") or {}).get(key) != expected for key, expected in expected_tags.items()):
            raise GateError("An observed resource is not owned by the exact bound generation.")
        state = value.get("properties", {}).get("provisioningState")
        if state is not None and state not in ("Succeeded", "Completed"):
            raise GateError("The actual provider operation has not succeeded.")
        self.values[identifier], self.versions[identifier] = value, version
        return value

    def bindings(self):
        return [{"id": key, "apiVersion": self.versions[key], "sha256": digest(value), "etag": value.get("etag")}
                for key, value in sorted(self.values.items())]


def consumer_owner(entry):
    return ({"deploymentId": entry["deploymentId"], "manifestSha256": entry["bindingHash"]}
            if entry["key"] == "fabric" else {"cohorsBinding": entry["bindingHash"]})


def observe_endpoint(observer, endpoint, allocation, owner):
    value = observer.get(endpoint["id"], owner=owner, kind="Microsoft.Network/privateEndpoints")
    properties = value["properties"]
    connections = properties.get("privateLinkServiceConnections", [])
    if (properties.get("subnet", {}).get("id") != endpoint["subnetId"]
            or properties.get("manualPrivateLinkServiceConnections") or len(connections) != 1):
        raise GateError("The private endpoint is not the exact automatically approved scoped connection.")
    connection = connections[0]["properties"]
    if (connection.get("privateLinkServiceId") != endpoint["serviceId"]
            or connection.get("groupIds") != endpoint["groupIds"]
            or connection.get("privateLinkServiceConnectionState", {}).get("status") != "Approved"):
        raise GateError("The actual private-link service/group/approval differs.")
    interfaces = properties.get("networkInterfaces", [])
    if len(interfaces) != 1:
        raise GateError("Expected one exact private-endpoint NIC.")
    nic = observer.get(interfaces[0]["id"], kind="Microsoft.Network/networkInterfaces")
    addresses, fqdns = set(), set()
    for item in nic.get("properties", {}).get("ipConfigurations", []):
        config = item["properties"]
        if config.get("subnet", {}).get("id") != endpoint["subnetId"] or config.get("publicIPAddress"):
            raise GateError("The endpoint NIC escaped its private subnet.")
        addresses.add(private_ip(config.get("privateIPAddress"), allocation))
        fqdns.update(config.get("privateLinkConnectionProperties", {}).get("fqdns", []))
    for config in properties.get("customDnsConfigs", []):
        if not set(config.get("ipAddresses", [])).issubset(addresses):
            raise GateError("Provider DNS/IP metadata disagrees with the endpoint NIC.")
        fqdns.add(config["fqdn"])
    if not addresses:
        raise GateError("The endpoint has no observed private addresses.")
    if "fqdns" in endpoint and sorted(fqdns) != sorted(endpoint["fqdns"]):
        raise GateError("The submitted endpoint names are not its actual provider-returned FQDN set.")
    return {"addresses": sorted(addresses), "fqdns": sorted(fqdns)}


def observe_hub(engine, stage, *, bound, bindings=None):
    from primary_bootstrap import stable_id
    manifest, client = engine.manifest, engine.client
    selected = bindings or empty_bindings(manifest)
    scopes = [hub_group(manifest)] + [entry["spoke"]["resourceGroupId"] for entry in selected["consumers"].values() if entry]
    observation = Observation(client, scopes)
    value = stage["hub"]
    if value["resourceGroupId"] != hub_group(manifest):
        raise GateError("The hub output is not this generation's owned resource group.")
    owner = manifest["bindingHash"]
    vnet = observation.get(value["vnetId"], owner=owner, kind="Microsoft.Network/virtualNetworks")
    if vnet.get("location") != manifest["location"] or vnet["properties"].get("addressSpace", {}).get("addressPrefixes") != [manifest["network"]["vnet"]]:
        raise GateError("Hub VNet region/address binding changed.")
    expected_subnets = {"firewall": "AzureFirewallSubnet", "gateway": "GatewaySubnet", "waf": "snet-waf", "endpoints": "snet-hub-endpoints"}
    subnets = {item["name"]: item for item in vnet["properties"].get("subnets", [])}
    if set(subnets) != set(expected_subnets.values()):
        raise GateError("The shared hub subnet set changed.")
    for role, name in expected_subnets.items():
        properties = subnets[name]["properties"]
        if (properties.get("addressPrefix") != manifest["network"][role]
                or subnets[name].get("id") != value["subnets"][role]["id"]):
            raise GateError("Hub subnet allocation changed.")
        if role in ("firewall", "gateway") and (properties.get("networkSecurityGroup") or properties.get("routeTable")):
            raise GateError("No blanket hub/default return UDR or NSG may be placed on Firewall/Gateway subnets.")
    if [item["properties"].get("serviceName") for item in subnets["snet-waf"]["properties"].get("delegations", [])] != ["Microsoft.Network/applicationGateways"]:
        raise GateError("Private-only WAF requires the actual Application Gateway subnet delegation.")
    for role, name in (("waf", "snet-waf"), ("endpoints", "snet-hub-endpoints")):
        properties = subnets[name]["properties"]
        nsg_id = properties.get("networkSecurityGroup", {}).get("id")
        arm_id(nsg_id, scope=hub_group(manifest), kind="Microsoft.Network/networkSecurityGroups")
        nsg = observation.get(nsg_id, owner=owner, kind="Microsoft.Network/networkSecurityGroups")
        validate_hub_nsg(nsg, role, manifest, selected, value, binding_stage=bindings is not None)
    if subnets["snet-hub-endpoints"]["properties"].get("privateEndpointNetworkPolicies") != "Enabled":
        raise GateError("Hub certificate endpoint requires its actual NSG network-policy enforcement.")
    route_id = subnets["snet-waf"]["properties"].get("routeTable", {}).get("id")
    if bindings is None:
        if route_id:
            raise GateError("Unexpected pre-consumer WAF routing.")
    else:
        arm_id(route_id, scope=hub_group(manifest), kind="Microsoft.Network/routeTables")
        routes = observation.get(route_id, owner=owner, kind="Microsoft.Network/routeTables")["properties"]
        items = routes.get("routes", [])
        if (routes.get("disableBgpRoutePropagation") is not False or len(items) != 1
                or any(items[0]["properties"].get(key) != expected for key, expected in {
                    "addressPrefix": "0.0.0.0/0", "nextHopType": "VirtualAppliance",
                    "nextHopIpAddress": value["firewallPrivateIp"]}.items())):
            raise GateError("WAF routing must retain specific private peering/gateway returns, not a blanket hub-prefix route.")
    firewall = observation.get(value["firewallId"], owner=owner, kind="Microsoft.Network/azureFirewalls")
    fw = firewall["properties"]
    configs = fw.get("ipConfigurations", [])
    if (fw.get("sku") != {"name": "AZFW_VNet", "tier": "Standard"} or len(configs) != 1
            or fw.get("firewallPolicy", {}).get("id") != value["firewallPolicyId"]
            or fw.get("autoscaleConfiguration") or fw.get("additionalProperties", {}).get("Network.SNAT.PrivateRanges")):
        raise GateError("Firewall SKU, policy, prescaling or private SNAT behavior differs from the reviewed shared edge.")
    configuration = configs[0]["properties"]
    actual_ip = private_ip(configuration.get("privateIPAddress"), manifest["network"]["firewall"])
    if (actual_ip != value["firewallPrivateIp"] or configuration.get("subnet", {}).get("id") != value["subnets"]["firewall"]["id"]
            or configuration.get("publicIPAddress", {}).get("id") != stage["edge"]["resources"]["firewallPublicIpId"]
            or firewall.get("zones") != manifest["network"]["zones"] or firewall.get("location") != manifest["location"]):
        raise GateError("Actual Firewall interface/IP differs from the preceding binding.")
    if vnet["properties"].get("dhcpOptions", {}).get("dnsServers", []) != ([actual_ip] if bound else []):
        raise GateError("Hub DNS is not bound to the actual Firewall proxy.")
    policy_value = observation.get(value["firewallPolicyId"], kind="Microsoft.Network/firewallPolicies")["properties"]
    if (policy_value.get("sku", {}).get("tier") != "Standard" or policy_value.get("threatIntelMode") != "Deny"
            or policy_value.get("dnsSettings", {}).get("enableProxy") is not True
            or policy_value.get("dnsSettings", {}).get("requireProxyForNetworkRules") is not True
            or policy_value.get("dnsSettings", {}).get("servers")):
        raise GateError("The shared firewall policy has an alternate resolver or weaker threat policy.")
    gateway = observation.get(value["vpnGatewayId"], owner=owner, kind="Microsoft.Network/virtualNetworkGateways")
    gw = gateway["properties"]
    if (gw.get("gatewayType") != "Vpn" or gw.get("vpnType") != "RouteBased"
            or gw.get("vpnGatewayGeneration") != "Generation1" or gw.get("enableBgp") is not False
            or gw.get("activeActive") is not False or gw.get("sku") != {"name": "VpnGw1AZ", "tier": "VpnGw1AZ"}):
        raise GateError("The shared gateway topology/SKU changed.")
    interfaces = gw.get("ipConfigurations", [])
    if (gateway.get("location") != manifest["location"] or len(interfaces) != 1
            or interfaces[0]["properties"].get("subnet", {}).get("id") != value["subnets"]["gateway"]["id"]
            or interfaces[0]["properties"].get("publicIPAddress", {}).get("id") != stage["edge"]["resources"]["gatewayPublicIpId"]):
        raise GateError("Shared VPN is not attached to its own exact GatewaySubnet/public IP.")
    vpn = engine.outputs["phase-identities"]["vpn"]
    verify_vpn_identity(client, manifest, vpn, stable_id(manifest, "scope:p2s_access"))
    client_config = gw.get("vpnClientConfiguration")
    if not bound and client_config:
        raise GateError("Unbound hub must not enable P2S authentication.")
    if bound:
        expected = {"aadAudience": vpn["clientId"], "aadIssuer": f"https://sts.windows.net/{manifest['binding']['tenantId']}/",
                    "vpnClientProtocols": ["OpenVPN"], "vpnAuthenticationTypes": ["AAD"],
                    "vpnClientAddressPool": {"addressPrefixes": [manifest["network"]["vpnClientPool"]]}}
        if (not client_config or any(client_config.get(key) != item for key, item in expected.items())
                or client_config.get("aadTenant", "").rstrip("/") != "https://login.microsoftonline.com/" + manifest["binding"]["tenantId"]):
            raise GateError("Shared P2S audience/protocol/pool is not the exact four-user contract.")
    for key in ("firewallPublicIpId", "gatewayPublicIpId"):
        pip = observation.get(stage["edge"]["resources"][key], owner=owner, kind="Microsoft.Network/publicIPAddresses")
        if (pip.get("sku") != {"name": "Standard", "tier": "Regional"}
                or pip["properties"].get("publicIPAllocationMethod") != "Static"
                or pip["properties"].get("publicIPAddressVersion") != "IPv4"
                or pip.get("zones") != manifest["network"]["zones"] or pip.get("location") != manifest["location"]):
            raise GateError("The required edge IP has an unreviewed SKU/allocation.")
    policy_input = hub_policy(manifest, selected, value if bound else None)
    observe_policy(client, manifest, {"edge": stage["edge"], "network": {"id": value["vnetId"]}},
                   policy_input, bound=bound, deny_transitive=True)
    for key in ("dnsPolicyId", "allowedDomainsId", "deniedDomainsId", "allowDnsRuleId", "denyDnsRuleId", "dnsLinkId",
                "privateDataRulesId", "httpsRulesId"):
        identifier = stage["edge"]["resources"].get(key)
        if identifier:
            observation.get(identifier, POLICY_API if key not in ("privateDataRulesId", "httpsRulesId") else NETWORK_API)
    dependencies = value["ownerDependencies"]
    identity = observation.get(dependencies["wafIdentityId"], "2024-11-30", owner=owner, kind="Microsoft.ManagedIdentity/userAssignedIdentities")
    if identity["properties"].get("principalId") != dependencies["wafIdentityPrincipalId"]:
        raise GateError("The WAF certificate identity principal changed.")
    vault = observation.get(dependencies["certificateVaultId"], "2024-11-01", owner=owner, kind="Microsoft.KeyVault/vaults")
    if (vault.get("location") != manifest["location"] or vault["properties"].get("publicNetworkAccess") != "Disabled"
            or vault["properties"].get("enableRbacAuthorization") is not True
            or vault["properties"].get("enablePurgeProtection") is not True
            or vault["properties"].get("enableSoftDelete") is not True
            or vault["properties"].get("softDeleteRetentionInDays") != 90
            or any(vault["properties"].get(key) is not False for key in
                   ("enabledForDeployment", "enabledForDiskEncryption", "enabledForTemplateDeployment"))
            or vault["properties"].get("networkAcls") != {"bypass": "None", "defaultAction": "Deny", "ipRules": [], "virtualNetworkRules": []}
            or vault["properties"].get("vaultUri") != dependencies["certificateVaultUri"]):
        raise GateError("The independent hub certificate vault is not private/RBAC/purge-protected in the same region.")
    endpoint = {"id": dependencies["certificateEndpointId"], "serviceId": dependencies["certificateVaultId"],
                "subnetId": value["subnets"]["endpoints"]["id"], "groupIds": ["vault"]}
    dependencies["certificateEndpointIps"] = observe_endpoint(observation, endpoint, manifest["network"]["endpoints"], owner)["addresses"]
    for zone in dependencies["zoneIds"]:
        observed = observation.get(zone["id"], DNS_API, owner=owner, kind="Microsoft.Network/privateDnsZones")
        if observed["name"] != zone["name"]:
            raise GateError("Hub DNS namespace/resource identity differs.")
        link = observation.get(zone["id"] + "/virtualNetworkLinks/hub", DNS_API)
        if link["properties"].get("registrationEnabled") is not False or link["properties"].get("virtualNetwork", {}).get("id") != value["vnetId"]:
            raise GateError("Shared DNS zone is not linked without registration to the exact resolver VNet.")
    allowed_peers = {f"{value['vnetId']}/virtualNetworkPeerings/to-{item['key']}" for item in active_consumers(selected)}
    actual_peers = client.collection(ARM + value["vnetId"] + "/virtualNetworkPeerings?api-version=" + NETWORK_API)
    if {item["id"] for item in actual_peers} != allowed_peers:
        raise GateError("Hub has an unbound or missing peering; no implicit transit/adoption.")
    return observation


def validate_hub_nsg(nsg, role, manifest, bindings, hub, *, binding_stage):
    rules = {item["name"]: item["properties"] for item in nsg["properties"].get("securityRules", [])}
    if len(rules) != len(nsg["properties"].get("securityRules", [])):
        raise GateError("Duplicate hub NSG rules.")
    expected = {}

    def rule(name, direction, access, protocol, source, destination, port, priority):
        destination_key = "destinationAddressPrefixes" if isinstance(destination, list) else "destinationAddressPrefix"
        expected[name] = {"direction": direction, "access": access, "protocol": protocol,
                          "sourceAddressPrefix": source, destination_key: destination,
                          "sourcePortRange": "*", "destinationPortRange": port, "priority": priority}

    if role == "endpoints":
        rule("WafPrivateVaultOnly", "Inbound", "Allow", "Tcp", manifest["network"]["waf"],
             manifest["network"]["endpoints"], "443", 100)
        rule("DenyOtherInbound", "Inbound", "Deny", "*", "*", "*", "*", 4096)
    else:
        web = [item for item in active_consumers(bindings) if item["web"]]
        if web or not binding_stage:
            rule("PrivateVpnHttpsOnly", "Inbound", "Allow", "Tcp", manifest["network"]["vpnClientPool"],
                 manifest["waf"]["frontendPrivateIp"], "443", 100)
        rule("RequiredAzureLoadBalancerHealth", "Inbound", "Allow", "*", "AzureLoadBalancer", "*", "*", 110)
        rule("DenyOtherInbound", "Inbound", "Deny", "*", "*", "*", "*", 4096)
        if binding_stage:
            if web:
                rule("PrivateBackendHttps", "Outbound", "Allow", "Tcp", manifest["network"]["waf"],
                     [item["web"]["backendPrivateIp"] for item in web] + hub["ownerDependencies"]["certificateEndpointIps"], "443", 100)
            rule("HubDnsProxyOnly", "Outbound", "Allow", "*", "*", hub["firewallPrivateIp"], "53", 110)
            rule("ReviewedHttpsViaFirewall", "Outbound", "Allow", "Tcp", "*", "Internet", "443", 120)
            rule("DenyPlatformDnsBypass", "Outbound", "Deny", "*", "*", "AzurePlatformDNS", "53", 130)
            rule("DenyOtherOutbound", "Outbound", "Deny", "*", "*", "*", "*", 4096)
        else:
            rule("DenyUnboundOutbound", "Outbound", "Deny", "*", "*", "*", "*", 4096)
    if set(rules) != set(expected):
        raise GateError("Missing or injected hub NSG rules.")
    for name, spec in expected.items():
        actual = rules[name]
        if any(actual.get(key) != value for key, value in spec.items()):
            raise GateError("Hub NSG sources, destinations, ports or priority differ.")
        if any(value not in (None, [], {}, "", False, "Succeeded") for key, value in actual.items()
               if key not in spec and key not in ("description",)):
            raise GateError("An unexpected hub NSG selector could broaden access.")


def read_peering(observation, identifier, remote, *, hub_side, use_remote):
    value = observation.get(identifier, kind="Microsoft.Network/virtualNetworks/virtualNetworkPeerings")
    expected = {"allowVirtualNetworkAccess": True, "allowForwardedTraffic": not hub_side,
                "allowGatewayTransit": hub_side, "useRemoteGateways": use_remote,
                "peeringState": "Connected"}
    if any(value["properties"].get(key) != item for key, item in expected.items()) or value["properties"].get("remoteVirtualNetwork", {}).get("id") != remote:
        raise GateError("The exact paired peering/transit flags have not been observed Connected.")
    return {"id": identifier, "remoteVirtualNetworkId": remote, **expected}


def observe_consumer(engine, observation, entry, hub, *, post_apply):
    consumer = entry["key"]
    spoke = entry["spoke"]
    owner = consumer_owner(entry)
    value = observation.get(spoke["vnetId"], owner=owner, kind="Microsoft.Network/virtualNetworks")
    properties = value["properties"]
    if value.get("location") != spoke["location"] or properties.get("addressSpace", {}).get("addressPrefixes") != [spoke["addressPrefix"]]:
        raise GateError("Actual spoke region/address binding differs.")
    if properties.get("dhcpOptions", {}).get("dnsServers") != [hub["firewallPrivateIp"]]:
        raise GateError("Spoke DNS is not the shared Firewall proxy.")
    actual_subnets = {item["id"].lower(): item for item in properties.get("subnets", [])}
    if set(actual_subnets) != {item["id"].lower() for item in spoke["subnets"].values()}:
        raise GateError("The spoke has extra/missing subnets, including a possible legacy gateway/firewall.")
    for role, subnet in spoke["subnets"].items():
        actual = actual_subnets[subnet["id"].lower()]["properties"]
        if actual.get("addressPrefix") != subnet["addressPrefix"]:
            raise GateError("Spoke subnet allocation changed.")
        route_id = actual.get("routeTable", {}).get("id")
        if role in ("containerApps", "workflows", "operations", "operator", "client", "agents") and not route_id:
            raise GateError("A workload subnet is missing its controlled-egress route association.")
        if route_id:
            arm_id(route_id, scope=spoke["resourceGroupId"], kind="Microsoft.Network/routeTables")
            route = observation.get(route_id, owner=owner, kind="Microsoft.Network/routeTables")["properties"]
            expected = {"addressPrefix": "0.0.0.0/0", "nextHopType": "VirtualAppliance", "nextHopIpAddress": hub["firewallPrivateIp"]}
            routes = route.get("routes", [])
            if route.get("disableBgpRoutePropagation") is not False or len(routes) != 1 or any(routes[0]["properties"].get(key) != item for key, item in expected.items()):
                raise GateError("No public fallback, broad hub-prefix route, VPN-via-Firewall asymmetry or unreviewed route is allowed.")
        if role != "bastion":
            nsg_id = actual.get("networkSecurityGroup", {}).get("id")
            if not nsg_id:
                raise GateError("Every workload/endpoint subnet requires an observed restrictive NSG.")
            arm_id(nsg_id, scope=spoke["resourceGroupId"], kind="Microsoft.Network/networkSecurityGroups")
            nsg = observation.get(nsg_id, owner=owner, kind="Microsoft.Network/networkSecurityGroups")
            validate_spoke_nsg(nsg, entry, hub, role, engine.manifest["network"]["vpnClientPool"])
        if role in ("privateEndpoints", "endpoints", "operatorEndpoints") and actual.get("privateEndpointNetworkPolicies") != "Enabled":
            raise GateError("PE NSG/routing enforcement is not actually enabled on the endpoint subnet.")
        if role in ("containerApps", "workflows", "operations", "agents"):
            if [item["properties"].get("serviceName") for item in actual.get("delegations", [])] != ["Microsoft.App/environments"]:
                raise GateError("The managed-host subnet delegation is not its reviewed provider contract.")
    peer_id = spoke["vnetId"] + "/virtualNetworkPeerings/to-shared-hub"
    peers = engine.client.collection(ARM + spoke["vnetId"] + "/virtualNetworkPeerings?api-version=" + NETWORK_API)
    if {item["id"] for item in peers} != {peer_id}:
        raise GateError("A spoke must have only its explicitly bound hub peering, not spoke-to-spoke or another gateway.")
    peering = None
    if post_apply:
        peering = {
            "hubToSpoke": read_peering(observation, hub["vnetId"] + "/virtualNetworkPeerings/to-" + consumer,
                                      spoke["vnetId"], hub_side=True, use_remote=False),
            "spokeToHub": read_peering(observation, peer_id, hub["vnetId"], hub_side=False,
                                      use_remote=entry["state"] != "peered"),
        }
    elif entry["state"] in ("connected", "data-bound", "web-bound"):
        read_peering(observation, peer_id, hub["vnetId"], hub_side=False, use_remote=True)
    elif entry["state"] == "peered":
        actual = observation.get(peer_id)["properties"]
        if (actual.get("useRemoteGateways") is not False or actual.get("allowGatewayTransit") is not False
                or actual.get("remoteVirtualNetwork", {}).get("id") != hub["vnetId"]
                or actual.get("allowVirtualNetworkAccess") is not True or actual.get("allowForwardedTraffic") is not True
                or actual.get("peeringState") not in ("Initiated", "Connected")):
            raise GateError("The first consumer peering must not enable remote transit ahead of the owner handshake.")
    endpoints = {}
    for endpoint in entry["privateEndpoints"]:
        allocation = next(item["addressPrefix"] for item in spoke["subnets"].values() if item["id"] == endpoint["subnetId"])
        endpoints[endpoint["id"]] = observe_endpoint(observation, endpoint, allocation, owner)
    for endpoint in entry["operatorEndpoints"]:
        observed = endpoints[endpoint["privateEndpointId"]]
        if endpoint["addresses"] != observed["addresses"] or endpoint["fqdns"] != observed["fqdns"]:
            raise GateError("Operator PE exceptions do not match the actual endpoint NIC/FQDNs.")
    for record in entry["dnsRecords"]:
        source = endpoints.get(record["sourceResourceId"])
        if source:
            if not set(record["addresses"]) <= set(source["addresses"]):
                raise GateError("A DNS record does not resolve to its own actual private endpoint.")
        else:
            environment = observation.get(record["sourceResourceId"], "2025-07-01", owner=owner,
                                           kind="Microsoft.App/managedEnvironments")
            actual = environment["properties"]
            if (actual.get("defaultDomain") != record["zone"] or record["name"] not in ("@", "*")
                    or record["addresses"] != [actual.get("staticIp")]
                    or actual.get("publicNetworkAccess") != "Disabled" or actual.get("vnetConfiguration", {}).get("internal") is not True):
                raise GateError("Generated Container Apps DNS must use its actual private domain/static IP.")
    if entry["web"]:
        if entry["web"]["backendKind"] == "PrivateEndpointWeb" and entry["web"]["backendPrivateIp"] not in endpoints.get(
                entry["web"]["backendResourceId"], {}).get("addresses", []):
            raise GateError("The HTTPS site backend address is not its actual private endpoint NIC address.")
        observe_web_backend(observation, entry)
    return peering


def validate_spoke_nsg(nsg, entry, hub, role, pool):
    rules = [item["properties"] for item in nsg["properties"].get("securityRules", [])]
    deny = [item for item in rules if item.get("direction") == "Inbound" and item.get("access") == "Deny"
            and item.get("sourceAddressPrefix") == "*" and item.get("destinationAddressPrefix") == "*"
            and item.get("destinationPortRange") == "*" and item.get("protocol") == "*"]
    if not deny or any(type(item.get("priority")) is not int or item["priority"] > 4096 for item in deny):
        raise GateError("The spoke NSG does not deny unmatched inbound traffic ahead of default VNet rules.")
    own = {item["addressPrefix"] for item in entry["spoke"]["subnets"].values()}
    for rule in rules:
        if rule.get("direction") != "Inbound" or rule.get("access") != "Allow":
            continue
        sources = rule.get("sourceAddressPrefixes") or [rule.get("sourceAddressPrefix")]
        if rule.get("sourceApplicationSecurityGroups") or rule.get("destinationApplicationSecurityGroups"):
            raise GateError("Unbound ASG selectors cannot replace explicit subnet/endpoint rules.")
        ports = rule.get("destinationPortRanges") or [rule.get("destinationPortRange")]
        if type(rule.get("priority")) is not int or rule["priority"] >= min(item["priority"] for item in deny):
            raise GateError("An inbound allow must precede the explicit deny, not rely on default VNet rules.")
        destinations = set(rule.get("destinationAddressPrefixes") or [rule.get("destinationAddressPrefix")])
        for source in sources:
            if source in own or source == "AzureLoadBalancer":
                continue
            if source == hub["subnets"]["waf"]["addressPrefix"] and role in ("containerApps", "endpoints") and set(ports) <= {"443", "31443"}:
                continue
            permitted = {ip for item in entry["operatorEndpoints"] for ip in item["addresses"]}
            if (entry["key"] == "fabric" and role == "operatorEndpoints" and source == pool
                    and ports == ["443"] and destinations and destinations <= permitted):
                continue
            raise GateError("NSG allows an unbound external/VPN/other-spoke source or a direct data/backend path.")
    outgoing_deny = any(rule.get("direction") == "Outbound" and rule.get("access") == "Deny"
                        and rule.get("destinationAddressPrefix") == "*" and rule.get("destinationPortRange") == "*"
                        for rule in rules)
    if role not in ("privateEndpoints", "endpoints", "operatorEndpoints") and not outgoing_deny:
        raise GateError("Workload NSGs must deny unmatched outbound traffic, including other spokes.")
    for rule in rules:
        if rule.get("direction") != "Outbound" or rule.get("access") != "Allow":
            continue
        destinations = rule.get("destinationAddressPrefixes") or [rule.get("destinationAddressPrefix")]
        ports = set(rule.get("destinationPortRanges") or [rule.get("destinationPortRange")])
        protocol = rule.get("protocol")
        for destination in destinations:
            if destination == hub["firewallPrivateIp"] and ports == {"53"} and protocol in ("*", "Tcp", "Udp"):
                continue
            if destination == "Internet" and ports == {"443"} and protocol == "Tcp":
                continue
            if destination == entry["spoke"]["addressPrefix"] or destination in own:
                if ports <= {"443", "1433"} and protocol == "Tcp":
                    continue
                if role in ("containerApps", "agents") and destination == entry["spoke"]["subnets"][role]["addressPrefix"]:
                    continue
            if destination == "168.63.129.16" and role in ("client", "operator") and ports <= {"80", "32526"} and protocol == "Tcp":
                continue
            raise GateError("An outbound NSG allow bypasses the reviewed own-data/DNS/HTTPS paths or enables another spoke.")


def observe_web_backend(observation, entry):
    web, spoke = entry["web"], entry["spoke"]
    if web["backendKind"] == "ContainerAppInternal":
        app = observation.get(web["backendResourceId"], "2025-07-01", owner=consumer_owner(entry), kind="Microsoft.App/containerApps")
        properties = app["properties"]
        ingress = properties.get("configuration", {}).get("ingress", {})
        if ingress.get("fqdn") != web["backendFqdn"] or ingress.get("allowInsecure") is not False or ingress.get("targetPort") != 8080:
            raise GateError("The human HTTPS backend is not its actual bound Container App ingress.")
        environment_id = properties.get("environmentId") or properties.get("managedEnvironmentId")
        environment = observation.get(environment_id, "2025-07-01", owner=consumer_owner(entry),
                                      kind="Microsoft.App/managedEnvironments")
        config = environment["properties"]
        if (environment.get("location") != spoke["location"] or config.get("staticIp") != web["backendPrivateIp"]
                or config.get("publicNetworkAccess") != "Disabled" or config.get("vnetConfiguration", {}).get("internal") is not True
                or config.get("vnetConfiguration", {}).get("infrastructureSubnetId") != spoke["subnets"]["containerApps"]["id"]):
            raise GateError("The WAF backend is not the actual private Container Apps environment.")
    else:
        endpoint = next((item for item in entry["privateEndpoints"] if item["id"] == web["backendResourceId"]), None)
        if not endpoint or endpoint["groupIds"] != ["sites"] or web["backendFqdn"].endswith(".fabric.microsoft.com"):
            raise GateError("A PE web backend requires its separately hosted HTTPS site, never Fabric MCP/Hosted/SaaS.")
        site = observation.get(endpoint["serviceId"], "2024-11-01", owner=consumer_owner(entry), kind="Microsoft.Web/sites")
        if (site.get("location") != spoke["location"] or site["properties"].get("httpsOnly") is not True
                or site["properties"].get("publicNetworkAccess") != "Disabled"
                or web["backendFqdn"] not in site["properties"].get("enabledHostNames", [])):
            raise GateError("The PE web backend is not HTTPS-only/private.")


def evidence_subject(entry):
    def without_evidence(value):
        if isinstance(value, dict):
            return {key: without_evidence(item) for key, item in value.items() if not key.endswith("Sha256") and key != "key"}
        if isinstance(value, list):
            return [without_evidence(item) for item in value]
        return value
    return digest(without_evidence(entry))


def check_evidence(directory, approval, manifest, entry, required):
    for purpose, expected in required:
        sha(expected, purpose)
        relative = Path(approval.get("hubEvidenceFiles", {}).get(expected, ""))
        if not relative.parts or relative.is_absolute() or ".." in relative.parts:
            raise GateError("Every hub prerequisite needs its exact existing private evidence file.")
        path = directory / relative
        if file_hash(path) != expected:
            raise GateError("Hub prerequisite evidence bytes changed.")
        evidence = read_json(path)
        now = utcnow()
        observed, expires = parse_time(evidence["observedAtUtc"]), parse_time(evidence["expiresAtUtc"])
        retirement = entry["state"] == "offboarded" and purpose in (
            "stoppedWorkEvidenceSha256", "credentialsAndGrantsRevokedEvidenceSha256", "spokePeeringAbsentEvidenceSha256")
        if retirement:
            expires = min(expires, observed + dt.timedelta(hours=24))
        else:
            expires = min(expires, parse_time(entry["expiresAtUtc"]))
        if (evidence.get("contract") != "cohors-hub-prerequisite-evidence-1"
                or evidence.get("generation") != manifest["generation"]
                or evidence.get("consumerBindingHash") != entry["bindingHash"]
                or evidence.get("sourceCommit") != entry["sourceCommit"]
                or evidence.get("purpose") != purpose or evidence.get("subjectSha256") != evidence_subject(entry)
                or evidence.get("passed") is not True
                or not observed <= now < expires):
            raise GateError("A prerequisite is stale, failed, unbound or from a different consumer/application configuration.")
        if purpose == "proxy-contract" and evidence.get("forwardedHeadersMode") not in ("not-used", "explicit-trusted-proxies"):
            raise GateError("Proxy review must explicitly preserve non-use or bind exact trusted proxies, never enable broad trust.")


def preflight(engine, plan, *, evidence_directory=None, approval=None):
    if plan["phase"] != "hub-bindings":
        return
    manifest = engine.manifest
    bindings = plan["bindingInput"]
    old = latest_hub_stage(engine.outputs)
    validate_bindings(bindings, manifest, previous_bindings(engine.outputs))
    observation = Observation(engine.client, [hub_group(manifest)] + [
        item["spoke"]["resourceGroupId"] for item in bindings["consumers"].values() if item])
    for consumer in active_consumers(bindings):
        observe_consumer(engine, observation, consumer, old["hub"], post_apply=False)
        prior = (previous_bindings(engine.outputs) or empty_bindings(manifest))["consumers"][consumer["key"]]
        if prior and prior["state"] not in ("reserved", "offboarded"):
            read_peering(observation, old["hub"]["vnetId"] + "/virtualNetworkPeerings/to-" + consumer["key"],
                         consumer["spoke"]["vnetId"], hub_side=True, use_remote=False)
        required = [("dns-chain", item["dnsEvidenceSha256"]) for item in consumer["dnsRecords"]]
        if consumer["web"]:
            web = consumer["web"]
            feature_id = (f"/subscriptions/{manifest['binding']['subscriptionId']}/providers/Microsoft.Features/"
                          "providers/Microsoft.Network/features/EnableApplicationGatewayNetworkIsolation")
            feature = engine.client.get(ARM + feature_id + "?api-version=2021-07-01")
            if feature.get("properties", {}).get("state") != "Registered":
                raise GateError("Private-only WAF network isolation is not Registered; this controller never registers it.")
            if not web["certificateSecretId"].startswith(old["hub"]["ownerDependencies"]["certificateVaultUri"].rstrip("/") + "/secrets/"):
                raise GateError("The frontend certificate must belong to the independently owned hub vault.")
            required += [("certificate", web["certificateEvidenceSha256"]), ("application-contract", web["applicationContractSha256"]),
                         ("proxy-contract", web["forwardedHeadersEvidenceSha256"]), ("private-dns", web["privateDnsEvidenceSha256"])]
        if evidence_directory is not None and consumer["key"] == bindings["changedConsumer"]:
            check_evidence(evidence_directory, approval, manifest, consumer, required)
    entry = bindings["consumers"][bindings["changedConsumer"]]
    if entry and entry["state"] == "offboarded":
        peer = entry["spoke"]["vnetId"] + "/virtualNetworkPeerings/to-shared-hub"
        _, _, status = engine.client.request("GET", ARM + peer + "?api-version=" + NETWORK_API, allowed=(200, 404))
        if status != 404:
            raise GateError("The consumer must stop its work and remove its own peering half before hub offboarding.")
        if evidence_directory is not None:
            check_evidence(evidence_directory, approval, manifest, entry,
                           [(key, value) for key, value in entry["offboarding"].items()])


def verify_checkpoint(client, manifest, outputs):
    from primary_bootstrap import Bootstrap
    if manifest["contract"] == PRIMARY_CONTRACT:
        receipt = latest_receipt(outputs)
        validate_receipt(receipt, manifest)
        verify_observations(client, receipt)
        return
    stage = latest_hub_stage(outputs)
    if not stage:
        raise GateError("No successful own hub baseline is available.")
    # Consumer-owned peering/PE changes are reviewed by their next binding revision, not adopted here.
    for item in stage.get("metadataReadback", {}).get("resources", []):
        if not item["id"].lower().startswith(hub_group(manifest).lower() + "/"):
            continue
        if item["id"] == stage["hub"]["vnetId"] or "/virtualNetworkPeerings/" in item["id"]:
            continue
        actual = client.get(ARM + item["id"] + "?api-version=" + item["apiVersion"])
        if digest(actual) != item["sha256"]:
            raise GateError("Shared hub state drifted after its checkpoint; stop instead of overwriting another owner's change.")
    engine = Bootstrap(manifest, type("ReadOnlyJournal", (), {"state": {"outputs": outputs}})(), client)
    observe_hub(engine, copy.deepcopy(stage), bound="phase-edge-bind" in outputs,
                bindings=previous_bindings(outputs))


def require_primary_phase_receipt(engine, phase):
    state = {"network": "reserved", "edge-bind": "peered", "foundation": "connected",
             "edge-policy": "data-bound", "runtime": "data-bound", "runtime-policy": "web-bound",
             "client-access": "web-bound", "activate": "web-bound"}.get(phase, "data-bound")
    receipt = latest_receipt(engine.outputs, required_state=state)
    validate_receipt(receipt, engine.manifest)
    verify_observations(engine.client, receipt)
    return receipt


def observe_primary_spoke(client, manifest, stage, receipt, *, remote_gateway):
    from primary_bootstrap import Bootstrap
    entry = {"key": "primary", "bindingHash": manifest["bindingHash"], "deploymentId": manifest["deploymentId"], "spoke": receipt["spoke"],
             "state": "connected" if remote_gateway else "peered", "privateEndpoints": [],
             "operatorEndpoints": [], "dnsRecords": [], "web": None}
    context = {**manifest, "network": {**manifest["network"], "vpnClientPool": receipt["vpn"]["clientPool"]}}
    observation = Observation(client, [receipt["spoke"]["resourceGroupId"], receipt["hub"]["resourceGroupId"]])
    engine = Bootstrap(context, type("ReadOnlyJournal", (), {"state": {"outputs": {}}})(), client)
    observe_consumer(engine, observation, entry, receipt["hub"], post_apply=remote_gateway)
    if not remote_gateway:
        peer = observation.get(stage["network"]["spokePeeringId"])
        if peer["properties"].get("useRemoteGateways") is not False:
            raise GateError("The initial spoke must not claim remote transit before the hub-side peering exists.")
    return {"resources": observation.bindings(), "trafficAccepted": False, "effectiveRoutesAccepted": False}


def deletion_version(identifier):
    lower = identifier.lower()
    if "/privatednszones/" in lower:
        return DNS_API
    if "/roleassignments/" in lower:
        return "2022-04-01"
    if "/applicationgateways/" in lower:
        return WAF_API
    return NETWORK_API


def delete_reference(engine, plan, identifier):
    from primary_bootstrap import assert_owned_id, phase_key
    assert_owned_id(identifier, engine.manifest)
    previous = latest_hub_stage(engine.outputs)
    proofs = {item["id"]: item for item in previous["metadataReadback"]["resources"]}
    if identifier not in proofs:
        raise GateError("No exact preceding owned observation authorizes reference deletion.")
    proof = proofs[identifier]
    operation = phase_key(plan) + "-release-" + digest(identifier)[:16]
    if operation not in engine.outputs:
        actual = engine.client.get(ARM + identifier + "?api-version=" + proof["apiVersion"])
        if "/virtualNetworkPeerings/" in identifier:
            consumer = plan["bindingInput"]["changedConsumer"]
            expected_remote = plan["bindingInput"]["consumers"][consumer]["spoke"]["vnetId"]
            properties = actual.get("properties", {})
            if (properties.get("remoteVirtualNetwork", {}).get("id") != expected_remote
                    or properties.get("allowGatewayTransit") is not True or properties.get("useRemoteGateways") is not False
                    or properties.get("allowForwardedTraffic") is not False):
                raise GateError("The retiring hub peering no longer belongs to this exact consumer.")
        elif digest(actual) != proof["sha256"]:
            raise GateError("The reference changed since the approved owner snapshot; no deletion.")
        engine.effect(operation, "DELETE", ARM + identifier + "?api-version=" + deletion_version(identifier),
                      allowed=(200, 202, 204), projection=lambda _: {"releasedReference": identifier},
                      headers={"If-Match": actual["etag"]} if actual.get("etag") else None)
    _, _, status = engine.client.request("GET", ARM + identifier + "?api-version=" + deletion_version(identifier), allowed=(200, 404))
    if status != 404:
        raise GateError("Reference deletion is not yet observed absent; no completed offboarding receipt.")


def release_last_waf(engine, plan):
    identifier = plan.get("deleteUnreferencedWaf")
    if identifier:
        if any(item["web"] for item in active_consumers(plan["bindingInput"])):
            raise GateError("A remaining consumer still references the shared WAF.")
        delete_reference(engine, plan, identifier)


def complete_hub_phase(engine, plan, result, compiled_hash):
    if plan["phase"] == "hub-bindings":
        release_last_waf(engine, plan)
        for identifier in plan["offboardReferences"]:
            delete_reference(engine, plan, identifier)
        result["bindingInput"] = plan["bindingInput"]
        if plan["offboardReferences"] or plan.get("deleteUnreferencedWaf"):
            from primary_bootstrap import phase_key
            from primary_release import checkpoint_inventory
            checkpoint_inventory(engine, phase_key(plan) + "-released", result)
    observer = observe_hub(engine, result, bound=plan["phase"] != "network", bindings=result.get("bindingInput"))
    for entry in active_consumers(result.get("bindingInput") or empty_bindings(engine.manifest)):
        observe_consumer(engine, observer, entry, result["hub"], post_apply=True)
    observe_binding_resources(engine, observer, result)
    from primary_bootstrap import phase_key
    result["metadataReadback"] = {"resources": observer.bindings(), "observedAtUtc": timestamp(),
                                 "compiledTemplateSha256": compiled_hash,
                                 "deploymentResourceId": engine.outputs["arm-" + phase_key(plan)]["id"],
                                 "infrastructureCommit": engine.manifest["releaseBundle"]["infrastructureCommit"],
                                 "trafficAccepted": False, "capacityAccepted": False, "costAccepted": False}


def observe_binding_resources(engine, observer, stage):
    bindings = stage.get("bindingInput") or empty_bindings(engine.manifest)
    for entry in active_consumers(bindings):
        for identifier in owned_references(stage, entry["key"]):
            actual = observer.get(identifier, deletion_version(identifier))
            if "/virtualNetworkLinks/" in identifier:
                if (actual["properties"].get("registrationEnabled") is not False
                        or actual["properties"].get("virtualNetwork", {}).get("id") != entry["spoke"]["vnetId"]):
                    raise GateError("A consumer DNS link has a different VNet or enabled autoregistration.")
        for record in entry["dnsRecords"]:
            identifier = hub_group(engine.manifest) + "/providers/Microsoft.Network/privateDnsZones/" + record["zone"] + "/A/" + record["name"]
            value = observer.get(identifier, DNS_API)["properties"]
            if (sorted(item["ipv4Address"] for item in value.get("aRecords", [])) != sorted(record["addresses"])
                    or value.get("metadata", {}).get("cohorsConsumer") != entry["bindingHash"]):
                raise GateError("Actual hub DNS record addresses/consumer ownership changed.")
        dynamic_zones = {record["zone"] for record in entry["dnsRecords"]}
        if entry["web"]:
            dynamic_zones.add(entry["web"]["frontendZone"])
            frontend = entry["web"]
            identifier = hub_group(engine.manifest) + "/providers/Microsoft.Network/privateDnsZones/" + frontend["frontendZone"] + "/A/" + frontend["frontendRecord"]
            actual = observer.get(identifier, DNS_API)["properties"]
            if (actual.get("aRecords") != [{"ipv4Address": engine.manifest["waf"]["frontendPrivateIp"]}]
                    or actual.get("metadata", {}).get("cohorsConsumer") != entry["bindingHash"]):
                raise GateError("The private client host does not resolve to the actual shared WAF address.")
        for zone in dynamic_zones:
            identifier = hub_group(engine.manifest) + "/providers/Microsoft.Network/privateDnsZones/" + zone
            observer.get(identifier, DNS_API, owner=engine.manifest["bindingHash"])
            link = observer.get(identifier + "/virtualNetworkLinks/hub", DNS_API)
            if link["properties"].get("registrationEnabled") is not False or link["properties"].get("virtualNetwork", {}).get("id") != stage["hub"]["vnetId"]:
                raise GateError("A generated private namespace is not linked to the exact hub resolver VNet.")
    waf_id = stage["hub"].get("wafId")
    if waf_id:
        observe_waf(engine, observer, stage)


def observe_waf(engine, observer, stage):
    manifest, hub = engine.manifest, stage["hub"]
    web = [item for item in active_consumers(stage["bindingInput"]) if item["web"]]
    value = observer.get(hub["wafId"], WAF_API, owner=manifest["bindingHash"], kind="Microsoft.Network/applicationGateways")
    properties = value["properties"]
    fronts = properties.get("frontendIPConfigurations", [])
    if (value.get("location") != manifest["location"] or properties.get("sku") != {"name": "WAF_v2", "tier": "WAF_v2", "capacity": manifest["waf"]["instanceCount"]}
            or properties.get("autoscaleConfiguration") or properties.get("privateLinkConfigurations")
            or len(fronts) != 1 or fronts[0]["properties"].get("publicIPAddress")
            or fronts[0]["properties"].get("privateIPAddress") != manifest["waf"]["frontendPrivateIp"]
            or fronts[0]["properties"].get("subnet", {}).get("id") != hub["subnets"]["waf"]["id"]):
        raise GateError("Actual WAF is not the selected private-only, fixed-capacity regional gateway.")
    if properties.get("sslPolicy", {}).get("policyName") != "AppGwSslPolicy20220101S":
        raise GateError("WAF TLS policy is not the reviewed TLS1.2+ policy.")
    if (value.get("identity", {}).get("type") != "UserAssigned"
            or set(value["identity"].get("userAssignedIdentities", {})) != {hub["ownerDependencies"]["wafIdentityId"]}
            or properties.get("enableHttp2") is not False
            or properties.get("globalConfiguration") != {"enableRequestBuffering": True, "enableResponseBuffering": True}
            or len(properties.get("frontendPorts", [])) != 1
            or properties["frontendPorts"][0]["properties"].get("port") != 443):
        raise GateError("WAF identity, transport/buffering or frontend ports changed.")
    listeners = {item["name"]: item["properties"] for item in properties.get("httpListeners", [])}
    backends = {item["name"]: item["properties"] for item in properties.get("backendAddressPools", [])}
    settings = {item["name"]: item["properties"] for item in properties.get("backendHttpSettingsCollection", [])}
    probes = {item["name"]: item["properties"] for item in properties.get("probes", [])}
    certificates = {item["name"]: item["properties"] for item in properties.get("sslCertificates", [])}
    routes = {item["name"]: item["properties"] for item in properties.get("requestRoutingRules", [])}
    if (set(listeners) != {"https-" + item["key"] for item in web} or set(settings) != set(listeners)
            or set(backends) != {"backend-" + item["key"] for item in web}
            or set(probes) != {"ready-" + item["key"] for item in web}
            or set(certificates) != {"cert-" + item["key"] for item in web}
            or set(routes) != {"route-" + item["key"] for item in web}):
        raise GateError("The shared WAF has extra/missing listeners, backends, settings or probes.")
    for entry in web:
        key, spec = entry["key"], entry["web"]
        listener, setting, probe = listeners["https-" + key], settings["https-" + key], probes["ready-" + key]
        if (listener.get("hostNames") != [spec["frontendHost"]] or listener.get("protocol") != "Https"
                or listener.get("requireServerNameIndication") is not True
                or backends["backend-" + key].get("backendAddresses") != [{"ipAddress": spec["backendPrivateIp"]}]
                or any(setting.get(name) != expected for name, expected in {
                    "protocol": "Https", "port": 443, "hostName": spec["backendFqdn"], "sniName": spec["backendFqdn"],
                    "validateSNI": True, "validateCertChainAndExpiry": True, "requestTimeout": spec["limits"]["requestTimeoutSeconds"]}.items())
                or probe.get("host") != spec["probe"]["host"] or probe.get("path") != spec["probe"]["path"]
                or setting.get("connectionDraining") != {"enabled": True, "drainTimeoutInSec": spec["limits"]["drainSeconds"]}
                or setting.get("cookieBasedAffinity") != "Disabled"
                or setting.get("pickHostNameFromBackendAddress") is not False
                or setting.get("probeEnabled") is not True
                or setting.get("probe", {}).get("id") != hub["wafId"] + "/probes/ready-" + key
                or probe.get("protocol") != "Https" or probe.get("port") != 443
                or probe.get("pickHostNameFromBackendHttpSettings") is not False
                or probe.get("match", {}).get("statusCodes") != ["200"]
                or certificates["cert-" + key].get("keyVaultSecretId") != spec["certificateSecretId"]
                or certificates["cert-" + key].get("data") or certificates["cert-" + key].get("password")):
            raise GateError("Actual WAF host/backend/SNI/TLS/probe settings differ from the consumer contract.")
        expected_route = {"ruleType": "Basic", "priority": 100 if key == "primary" else 200,
                          "httpListener": {"id": hub["wafId"] + "/httpListeners/https-" + key},
                          "backendAddressPool": {"id": hub["wafId"] + "/backendAddressPools/backend-" + key},
                          "backendHttpSettings": {"id": hub["wafId"] + "/backendHttpSettingsCollection/https-" + key}}
        if any(routes["route-" + key].get(name) != expected for name, expected in expected_route.items()):
            raise GateError("A host listener routes to a different consumer backend.")
        roots = {item["name"]: item["properties"].get("data") for item in properties.get("trustedRootCertificates", [])}
        expected_root = [] if spec["backendRootCertificate"] is None else [{"id": hub["wafId"] + "/trustedRootCertificates/root-" + key}]
        if setting.get("trustedRootCertificates", []) != expected_root or (expected_root and roots.get("root-" + key) != spec["backendRootCertificate"]):
            raise GateError("The backend CA binding changed or adds an unreviewed trusted root.")
        policy_id = listener.get("firewallPolicy", {}).get("id")
        policy = observer.get(policy_id, NETWORK_API, owner=manifest["bindingHash"],
                              kind="Microsoft.Network/ApplicationGatewayWebApplicationFirewallPolicies")["properties"]
        options = policy.get("policySettings", {})
        if (options.get("mode") != "Prevention" or options.get("state") != "Enabled"
                or any(options.get(name) is not True for name in ("requestBodyCheck", "requestBodyEnforcement", "fileUploadEnforcement"))
                or options.get("maxRequestBodySizeInKb") != spec["limits"]["bodyKb"]
                or options.get("requestBodyInspectLimitInKB") != spec["limits"]["bodyKb"]
                or options.get("fileUploadLimitInMb") != spec["limits"]["uploadMb"]
                or options.get("logScrubbing", {}).get("state") != "Enabled"
                or policy.get("managedRules", {}).get("exclusions")
                or policy.get("managedRules", {}).get("managedRuleSets") != [
                    {"ruleSetType": "OWASP", "ruleSetVersion": "3.2", "ruleGroupOverrides": []}]):
            raise GateError("WAF inspection, size enforcement or logging protection weakened.")
        custom = policy.get("customRules", [])
        if (len(custom) != 1 or custom[0].get("action") != "Block" or custom[0].get("priority") != 1
                or custom[0].get("ruleType") != "MatchRule" or custom[0].get("state") != "Enabled"
                or custom[0].get("matchConditions") != [{"matchVariables": [{"variableName": "RemoteAddr"}],
                     "operator": "IPMatch", "negationConditon": True, "matchValues": [manifest["network"]["vpnClientPool"]]}]):
            raise GateError("WAF must block non-VPN sources without a short-circuit Allow rule.")
        scrubbing = options["logScrubbing"].get("scrubbingRules", [])
        if ({item.get("matchVariable") for item in scrubbing} != {"RequestIPAddress", "RequestHeaderNames", "RequestCookieNames",
                                                               "RequestArgNames", "RequestPostArgNames", "RequestJSONArgNames"}
                or len(scrubbing) != 6 or any(item.get("state") != "Enabled" or item.get("selectorMatchOperator") != "EqualsAny"
                                             or item.get("selector") != "*" for item in scrubbing)):
            raise GateError("WAF sensitive-data scrubbing differs from the reviewed policy.")
        reference = next(item for item in stage["bindings"]["web"] if item["consumer"] == key)
        if policy_id != reference["policyId"]:
            raise GateError("The listener policy is not this consumer's owned policy.")
        grant = observer.get(reference["certificateGrantId"], "2022-04-01")["properties"]
        secret_name = spec["certificateSecretId"].split("/")[4]
        secret_scope = hub["ownerDependencies"]["certificateVaultId"] + "/secrets/" + secret_name
        if (not reference["certificateGrantId"].startswith(secret_scope + "/providers/Microsoft.Authorization/roleAssignments/")
                or grant.get("principalId") != hub["ownerDependencies"]["wafIdentityPrincipalId"]
                or grant.get("principalType") != "ServicePrincipal"
                or grant.get("roleDefinitionId") != "/subscriptions/" + manifest["binding"]["subscriptionId"]
                   + "/providers/Microsoft.Authorization/roleDefinitions/4633458b-17de-408a-b874-0445c86b69e6"):
            raise GateError("WAF certificate read permission is not the exact hub identity and per-consumer secret scope.")
    diagnostic_settings = engine.client.collection(ARM + hub["wafId"] + "/providers/Microsoft.Insights/diagnosticSettings?api-version=2021-05-01-preview")
    if any(log.get("enabled") for setting in diagnostic_settings for log in setting.get("properties", {}).get("logs", [])):
        raise GateError("Request/access logs are not approved by the metrics-only privacy contract.")


def make_receipt(engine, consumer, stage, observer):
    entry = stage["bindingInput"]["consumers"].get(consumer)
    if not entry:
        raise GateError("No explicitly reserved consumer exists in this hub revision.")
    item = {"key": consumer, **entry}
    peering = observe_consumer(engine, observer, item, stage["hub"], post_apply=True) if entry["state"] not in ("reserved", "offboarded") else None
    vpn = engine.outputs["phase-identities"]["vpn"]
    scopes = (stage["hub"]["resourceGroupId"], entry["spoke"]["resourceGroupId"])
    readback = {**stage["metadataReadback"], "observedAtUtc": timestamp(), "resources": [
        item for item in observer.bindings() if any(item["id"].lower().startswith(scope.lower() + "/") for scope in scopes)]}
    hub = {key: value for key, value in stage["hub"].items() if key != "ownerDependencies"}
    zones = set(engine.manifest["dnsZones"]) | {record["zone"] for record in entry["dnsRecords"]}
    if entry["web"]:
        zones.add(entry["web"]["frontendZone"])
    receipt = {
        "contract": RECEIPT_CONTRACT, "generation": engine.manifest["generation"],
        "revision": stage["bindingInput"]["revision"], "hubBindingHash": engine.manifest["bindingHash"],
        "consumer": consumer, "consumerBindingHash": entry["bindingHash"], "state": entry["state"],
        "expiresAtUtc": entry["expiresAtUtc"], "hub": hub, "spoke": entry["spoke"], "peering": peering,
        "vpn": {"audienceClientId": vpn["clientId"], "servicePrincipalObjectId": vpn["servicePrincipalObjectId"],
                "assignmentRequired": True, "userSetSha256": digest(sorted(engine.manifest["vpnUserObjectIds"])),
                "resolvedUsersEvidenceSha256": engine.manifest["resolvedUsersEvidenceSha256"], "businessRolesChanged": False,
                "clientPool": engine.manifest["network"]["vpnClientPool"]},
        "dns": {"owner": "hub", "registrationMode": "hub-owner-only-no-consumer-zone-groups",
                "zones": [{"name": zone, "id": hub_group(engine.manifest) + "/providers/Microsoft.Network/privateDnsZones/" + zone} for zone in sorted(zones)],
                "recordSets": sorted(item["id"] for category in ("dataRecords", "frontendRecords")
                                     for item in stage.get("bindings", {}).get(category, []) if item["consumer"] == consumer)},
        "egress": {"defaultNextHop": "VirtualAppliance", "nextHopIp": hub["firewallPrivateIp"], "bgpPropagation": True,
                   "hubPrefixRoute": False, "spokeToSpoke": False, "vpnDatabaseAccess": False,
                   "dnsDomains": entry["edgePolicy"]["dnsDomains"],
                   "httpsRules": [] if entry["state"] == "offboarded" else [
                       {**rule, "name": consumer + "-" + rule["name"]} for rule in policy(
                           entry["edgePolicy"], {key: subnet["addressPrefix"] for key, subnet in entry["spoke"]["subnets"].items()})["httpsRules"]]},
        "web": entry["web"], "operatorEndpoints": entry["operatorEndpoints"], "ownership": OWNERSHIP, "readback": readback,
    }
    receipt["receiptSha256"] = digest(receipt)
    validate_wire_receipt(receipt)
    return receipt


def exchange_receipt(engine, args):
    if args.command == "bind-hub":
        if engine.manifest["contract"] != PRIMARY_CONTRACT or not args.input:
            raise GateError("Only the version3 Primary consumer imports a --input hub receipt.")
        receipt = read_json(args.input)
        validate_receipt(receipt, engine.manifest, allow_offboarded=True)
        verify_observations(engine.client, receipt)
        prior = [value for key, value in engine.outputs.items() if key.startswith("hub-receipt-")]
        if prior:
            previous = max(prior, key=lambda value: (value["revision"], parse_time(value["readback"]["observedAtUtc"])))
            if receipt["revision"] < previous["revision"]:
                raise GateError("A hub receipt revision cannot roll back.")
            if receipt["revision"] == previous["revision"] and receipt != previous:
                refreshed = copy.deepcopy(previous)
                refreshed["readback"]["observedAtUtc"] = receipt["readback"]["observedAtUtc"]
                refreshed["receiptSha256"] = receipt["receiptSha256"]
                if refreshed != receipt or parse_time(receipt["readback"]["observedAtUtc"]) <= parse_time(previous["readback"]["observedAtUtc"]):
                    raise GateError("Same-revision refresh may only advance the readback timestamp, never rebind resources.")
        operation = f"hub-receipt-{receipt['revision']:06d}-{receipt['receiptSha256'][:16]}"
        if engine.journal.begin(operation, "READ-BOUND-HUB", receipt["hub"]["vnetId"], {"sha256": receipt["receiptSha256"]}):
            engine.journal.complete(operation, receipt)
        return
    if engine.manifest["contract"] != HUB_CONTRACT or not args.consumer or not args.out:
        raise GateError("Only the hub owner exports a specific --consumer receipt to a new private --out.")
    stage = copy.deepcopy(latest_hub_stage(engine.outputs))
    if not stage or not stage.get("bindingInput"):
        raise GateError("No successful hub binding revision exists.")
    verify_checkpoint(engine.client, engine.manifest, engine.outputs)
    observer = observe_hub(engine, stage, bound=True, bindings=stage["bindingInput"])
    observe_binding_resources(engine, observer, stage)
    receipt = make_receipt(engine, args.consumer, stage, observer)
    write_json(args.out, receipt)


def require_retirement(engine):
    if any(key.startswith("arm-hub-bindings-") and "phase-" + key.removeprefix("arm-") not in engine.outputs
           for key in engine.outputs):
        raise GateError("A partially completed shared revision must be reconciled before hub retirement.")
    bindings = previous_bindings(engine.outputs)
    if not bindings:
        return
    if any(entry and entry["state"] != "offboarded" for entry in bindings["consumers"].values()):
        raise GateError("Shared hub stop/retirement is blocked while either consumer remains reserved or attached.")
    stage = latest_hub_stage(engine.outputs)
    if any(stage.get("bindings", {}).get(key) for key in ("peers", "dnsLinks", "dataRecords", "frontendRecords", "web")) or stage["hub"].get("wafId"):
        raise GateError("Hub retirement requires observed release of every exact consumer reference.")
    peers = engine.client.collection(ARM + stage["hub"]["vnetId"] + "/virtualNetworkPeerings?api-version=" + NETWORK_API)
    if peers:
        raise GateError("Hub retirement found a remaining peering; no shared resource deletion.")


def require_consumer_offboarding(engine):
    receipt = latest_receipt(engine.outputs, required_state="offboarded")
    validate_receipt(receipt, engine.manifest, allow_offboarded=True)
    verify_observations(engine.client, receipt)
    _, _, status = engine.client.request("GET", ARM + receipt["hub"]["vnetId"] + "/virtualNetworkPeerings/to-primary?api-version=" + NETWORK_API, allowed=(200, 404))
    if status != 404:
        raise GateError("Primary cleanup must wait for actual hub-owner reference release.")


def bind_spa_origin(engine):
    receipt = require_primary_phase_receipt(engine, "activate")
    origin = "https://" + receipt["web"]["frontendHost"]
    application = engine.outputs["phase-identities"]["spa"]
    from primary_bootstrap import ownership_tags
    actual = engine.client.get(GRAPH + "/v1.0/applications/" + application["objectId"] + "?$select=id,tags")
    if not set(ownership_tags(engine.manifest, "spa")) <= set(actual.get("tags", [])):
        raise GateError("SPA ownership drift prevents redirect mutation.")
    engine.effect("spa-private-waf-origin", "PATCH", GRAPH + "/v1.0/applications/" + application["objectId"],
                  {"spa": {"redirectUris": [origin]}})
    value = engine.client.get(GRAPH + "/v1.0/applications/" + application["objectId"] + "?$select=id,spa")
    if value.get("spa", {}).get("redirectUris") != [origin]:
        raise GateError("SPA redirect must equal the actual private WAF client origin, not the internal backend SNI.")


def detach_primary(engine, approval, directory):
    manifest = engine.manifest
    if manifest["contract"] != PRIMARY_CONTRACT or "phase-stop" not in engine.outputs:
        raise GateError("Only a stopped version3 Primary may detach its own peering.")
    receipt = latest_receipt(engine.outputs)
    evidence_file = Path(approval["evidenceFiles"]["readinessEvidenceSha256"])
    evidence = read_json(directory / evidence_file)
    if (evidence.get("contract") != "cohors-primary-offboarding-evidence-1"
            or evidence.get("bindingHash") != manifest["bindingHash"]
            or evidence.get("applicationCommit") != manifest["releaseBundle"]["applicationCommit"]
            or evidence.get("stopOutputSha256") != digest(engine.outputs["phase-stop"])
            or evidence.get("workersAndCallbacksStopped") is not True
            or evidence.get("ownCredentialsAndGrantsRevoked") is not True
            or evidence.get("outstandingTokenWindowElapsed") is not True
            or not parse_time(evidence["observedAtUtc"]) <= utcnow() < parse_time(evidence["validUntilUtc"])):
        raise GateError("Own work, credential/grant revocation and outstanding tokens require source-bound offboarding evidence.")
    identifier = receipt["spoke"]["vnetId"] + "/virtualNetworkPeerings/to-shared-hub"
    from primary_bootstrap import assert_owned_id, check_owned_groups
    assert_owned_id(identifier, manifest)
    check_owned_groups(engine.client, manifest, must_exist=True)
    vnet = engine.client.get(ARM + receipt["spoke"]["vnetId"] + "?api-version=" + NETWORK_API)
    if (vnet.get("tags") or {}).get("cohorsBinding") != manifest["bindingHash"]:
        raise GateError("Own VNet ownership changed; do not detach an adopted peering.")
    operation = "detach-own-shared-hub-peer"
    if operation not in engine.outputs:
        value, _, status = engine.client.request("GET", ARM + identifier + "?api-version=" + NETWORK_API, allowed=(200, 404))
        if status != 404:
            if value.get("id") != identifier or value.get("properties", {}).get("remoteVirtualNetwork", {}).get("id") != receipt["hub"]["vnetId"]:
                raise GateError("The own peering no longer targets this shared hub.")
            engine.effect(operation, "DELETE", ARM + identifier + "?api-version=" + NETWORK_API,
                          allowed=(200, 202, 204), projection=lambda _: {"releasedReference": identifier},
                          headers={"If-Match": value["etag"]} if value.get("etag") else None)
    _, _, status = engine.client.request("GET", ARM + identifier + "?api-version=" + NETWORK_API, allowed=(200, 404))
    if status != 404:
        raise GateError("Own peering removal has not completed.")
    from primary_release import checkpoint_inventory
    checkpoint_inventory(engine, "detach-hub")
    return {"ownSpokePeeringAbsent": identifier, "offboardingEvidenceSha256": approval["readinessEvidenceSha256"],
            "sharedResourcesMutated": False, "awaitingHubOwnerRelease": True}


def verify_receipt_resources(receipt, resources):
    def resource(identifier):
        if identifier not in resources:
            raise GateError("Receipt omits a required actual resource relationship.")
        return resources[identifier]

    hub, spoke = receipt["hub"], receipt["spoke"]
    vnet = resource(hub["vnetId"])
    if ((vnet.get("tags") or {}).get("cohorsBinding") != receipt["hubBindingHash"]
            or vnet.get("location") != hub["location"]
            or vnet["properties"].get("addressSpace", {}).get("addressPrefixes") != [hub["addressPrefix"]]
            or vnet["properties"].get("dhcpOptions", {}).get("dnsServers") != [hub["firewallPrivateIp"]]):
        raise GateError("Hub receipt topology does not match the actual owned VNet.")
    subnets = {item["id"]: item["properties"] for item in vnet["properties"].get("subnets", [])}
    for role, subnet in hub["subnets"].items():
        if subnets.get(subnet["id"], {}).get("addressPrefix") != subnet["addressPrefix"]:
            raise GateError("A receipt hub subnet differs from its actual allocation.")
    firewall = resource(hub["firewallId"])
    interfaces = firewall["properties"].get("ipConfigurations", [])
    if ((firewall.get("tags") or {}).get("cohorsBinding") != receipt["hubBindingHash"]
            or firewall["properties"].get("sku") != {"name": "AZFW_VNet", "tier": "Standard"} or len(interfaces) != 1
            or interfaces[0]["properties"].get("privateIPAddress") != hub["firewallPrivateIp"]
            or interfaces[0]["properties"].get("subnet", {}).get("id") != hub["subnets"]["firewall"]["id"]
            or firewall["properties"].get("firewallPolicy", {}).get("id") != hub["firewallPolicyId"]):
        raise GateError("A receipt cannot substitute another Firewall IP/interface/policy.")
    policy_value = resource(hub["firewallPolicyId"])["properties"]
    if (policy_value.get("sku", {}).get("tier") != "Standard" or policy_value.get("threatIntelMode") != "Deny"
            or policy_value.get("dnsSettings", {}).get("enableProxy") is not True
            or policy_value.get("dnsSettings", {}).get("requireProxyForNetworkRules") is not True
            or policy_value.get("dnsSettings", {}).get("servers")):
        raise GateError("Observed Firewall policy is not the bound DNS-proxy/deny policy.")
    gateway = resource(hub["vpnGatewayId"])
    vpn = gateway["properties"].get("vpnClientConfiguration", {})
    if ((gateway.get("tags") or {}).get("cohorsBinding") != receipt["hubBindingHash"]
            or vpn.get("aadAudience") != receipt["vpn"]["audienceClientId"]
            or vpn.get("vpnClientAddressPool", {}).get("addressPrefixes") != [receipt["vpn"]["clientPool"]]
            or vpn.get("vpnClientProtocols") != ["OpenVPN"] or vpn.get("vpnAuthenticationTypes") != ["AAD"]):
        raise GateError("The receipt VPN audience/pool is not the actual shared gateway configuration.")
    for zone in receipt["dns"]["zones"]:
        actual = resource(zone["id"])
        if actual.get("name") != zone["name"] or (actual.get("tags") or {}).get("cohorsBinding") != receipt["hubBindingHash"]:
            raise GateError("The private namespace is not the exact hub-owned zone.")
    for identifier in receipt["dns"]["recordSets"]:
        record = resource(identifier)["properties"]
        if record.get("metadata", {}).get("cohorsConsumer") != receipt["consumerBindingHash"]:
            raise GateError("DNS record is not scoped to this exact consumer binding.")
    actual_rules = []
    group = resource(hub["firewallPolicyId"] + "/ruleCollectionGroups/reviewed-https")["properties"]
    for collection in group.get("ruleCollections", []):
        for rule in collection.get("rules", []):
            if rule.get("name", "").startswith(receipt["consumer"] + "-"):
                if rule.get("protocols") != [{"protocolType": "Https", "port": 443}] or rule.get("terminateTLS") is not False:
                    raise GateError("Actual consumer egress has an unreviewed protocol or TLS interception.")
                actual_rules.append({key: rule[key] for key in ("name", "sourceAddresses", "targetFqdns")})
    expected_rules = receipt["egress"]["httpsRules"] if receipt["state"] not in ("reserved", "offboarded") else []
    if sorted(actual_rules, key=lambda item: item["name"]) != sorted(expected_rules, key=lambda item: item["name"]):
        raise GateError("Desired versus installed consumer HTTPS rules disagree with the receipt state.")
    if receipt["state"] in ("reserved", "offboarded"):
        return
    actual = resource(spoke["vnetId"])
    owner = {"manifestSha256": receipt["consumerBindingHash"]} if receipt["consumer"] == "fabric" else {"cohorsBinding": receipt["consumerBindingHash"]}
    if (any((actual.get("tags") or {}).get(key) != value for key, value in owner.items())
            or actual["properties"].get("addressSpace", {}).get("addressPrefixes") != [spoke["addressPrefix"]]
            or actual["properties"].get("dhcpOptions", {}).get("dnsServers") != [hub["dnsServerIp"]]):
        raise GateError("Actual spoke is not the receipt's owned DNS/address binding.")
    entry = {"key": receipt["consumer"], "spoke": spoke, "operatorEndpoints": receipt["operatorEndpoints"]}
    subnets = {item["id"]: item["properties"] for item in actual["properties"].get("subnets", [])}
    if set(subnets) != {item["id"] for item in spoke["subnets"].values()}:
        raise GateError("Actual spoke subnet set differs from the receipt.")
    for role, subnet in spoke["subnets"].items():
        properties = subnets[subnet["id"]]
        if properties.get("addressPrefix") != subnet["addressPrefix"]:
            raise GateError("Actual spoke subnet prefix differs.")
        if role != "bastion":
            nsg = resource(properties.get("networkSecurityGroup", {}).get("id"))
            validate_spoke_nsg(nsg, entry, hub, role, receipt["vpn"]["clientPool"])
        route_id = properties.get("routeTable", {}).get("id")
        if role in ("containerApps", "workflows", "operations", "operator", "client", "agents") and not route_id:
            raise GateError("Required workload route association is missing.")
        if route_id:
            routes = resource(route_id)["properties"]
            rules = routes.get("routes", [])
            if (routes.get("disableBgpRoutePropagation") is not False or len(rules) != 1
                    or any(rules[0]["properties"].get(key) != value for key, value in {
                        "addressPrefix": "0.0.0.0/0", "nextHopType": "VirtualAppliance", "nextHopIpAddress": hub["firewallPrivateIp"]}.items())):
                raise GateError("Receipt route is not the exact controlled-egress default with BGP propagation.")
