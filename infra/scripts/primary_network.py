"""Primary-only managed-edge contracts and readbacks; metadata is not traffic acceptance."""

import ipaddress
import re
import urllib.parse

from primary_control import ARM, GRAPH, GateError, digest, require_guid


NETWORK_VERSION = "2024-07-01"
APP_VERSION = "2025-07-01"
VPN_CLIENT = "c632b3df-fb67-4d84-bdcf-b95ad541b5c8"
DEFAULT_APP_ROLE = "00000000-0000-0000-0000-000000000000"
SOURCE_SEGMENTS = ("containerApps", "workflows", "operations", "operator", "client")
SUBNETS = ("containerApps", "workflows", "operations", "privateEndpoints",
           "operator", "client", "bastion", "firewall", "gateway")
HOST = re.compile(r"(?=.{1,253}\Z)(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+"
                  r"[a-z](?:[a-z0-9-]{0,61}[a-z0-9])?\Z")


def hostname(value, *, wildcard=False):
    if not isinstance(value, str):
        raise GateError("A reviewed DNS/FQDN value must be a string.")
    candidate = value[2:] if wildcard and value.startswith("*.") else value
    if not HOST.fullmatch(candidate):
        raise GateError("DNS/FQDN values must be canonical lowercase names, not URLs, IPs or catch-all rules.")
    return value


def endpoint_host(value):
    if not isinstance(value, str):
        raise GateError("A generated private endpoint binding is missing.")
    if "://" not in value:
        return hostname(value)
    try:
        parsed = urllib.parse.urlsplit(value)
        port = parsed.port
    except ValueError as error:
        raise GateError("The generated endpoint is not a valid HTTPS service URL.") from error
    if (parsed.scheme != "https" or not parsed.hostname or parsed.username or parsed.password
            or port is not None or parsed.query or parsed.fragment):
        raise GateError("Only generated HTTPS service endpoints may extend the private DNS policy.")
    return hostname(parsed.hostname)


def observe_private_service_endpoints(client, manifest, platform):
    from primary_bootstrap import assert_owned_id
    services = platform.get("privateServiceEndpoints")
    if (not isinstance(services, dict) or set(services) != {"registryData", "telemetryResourceId"}
            or not isinstance(services["registryData"], list) or not services["registryData"]
            or services["telemetryResourceId"] != platform["resources"]["applicationInsights"]):
        raise GateError("Actual provider ACR endpoints and the exact own telemetry component ID are required.")
    identifier = services["telemetryResourceId"]
    assert_owned_id(identifier, manifest)
    actual = client.get(ARM + identifier + "?api-version=2020-02-02")
    if (actual.get("id") != identifier or actual.get("type", "").lower() != "microsoft.insights/components"
            or (actual.get("tags") or {}).get("cohorsBinding") != manifest["bindingHash"]
            or actual.get("location") != manifest["location"]
            or actual.get("properties", {}).get("provisioningState") != "Succeeded"
            or actual["properties"].get("DisableLocalAuth") is not True):
        raise GateError("The telemetry component is not the successful, owned, identity-only deployment.")
    connection = actual["properties"].get("ConnectionString")
    if not isinstance(connection, str):
        raise GateError("The provider has not returned the actual regional telemetry endpoints.")
    endpoints = {}
    for part in connection.split(";"):
        key, separator, value = part.partition("=")
        if key in ("IngestionEndpoint", "LiveEndpoint"):
            if not separator or key in endpoints:
                raise GateError("Duplicate or incomplete telemetry endpoint metadata.")
            endpoint_host(value)
            endpoints[key] = value
    if set(endpoints) != {"IngestionEndpoint", "LiveEndpoint"}:
        raise GateError("Actual ingestion and live telemetry endpoints are both required; no synthesized regional aliases.")
    for value in services["registryData"]:
        hostname(value)
    projected = {"registryData": services["registryData"], "telemetry": [endpoints[key] for key in sorted(endpoints)]}
    platform["privateServiceEndpoints"] = projected
    return {"componentId": identifier, "apiVersion": "2020-02-02", "endpointProjectionSha256": digest(projected),
            "instrumentationIdentifiersPersisted": False, "trafficAccepted": False}


def validate_network_manifest(manifest):
    if manifest.get("azureFirewall") is not True or manifest.get("vpn") is not True:
        raise GateError("The versioned primary profile requires the selected own Firewall and P2S gateway.")
    if manifest.get("containerAppsProfile") != "D4":
        raise GateError("The primary working profile is explicitly D4; no automatic Consumption fallback.")
    network = manifest.get("network")
    if not isinstance(network, dict) or set(network) != {"vnet", "vpnClientPool", "zones", *SUBNETS}:
        raise GateError("Use the managed-edge network contract, not legacy NVA fields or unreviewed extensions.")
    if "nvaPolicy" in manifest:
        raise GateError("The managed primary edge cannot also activate a legacy NVA policy.")
    ranges = {}
    for key in ("vnet", "vpnClientPool", *SUBNETS):
        try:
            if not isinstance(network[key], str):
                raise ValueError()
            ranges[key] = ipaddress.ip_network(network[key], strict=True)
        except ValueError as error:
            raise GateError("Primary address ranges must be canonical IPv4 CIDRs.") from error
        if ranges[key].version != 4 or not any(ranges[key].subnet_of(block) for block in (
                ipaddress.ip_network("10.0.0.0/8"), ipaddress.ip_network("172.16.0.0/12"),
                ipaddress.ip_network("192.168.0.0/16"))):
            raise GateError("Primary network and client ranges must use reviewed RFC1918 IPv4 space.")
    children = [ranges[key] for key in SUBNETS]
    if any(not child.subnet_of(ranges["vnet"]) for child in children):
        raise GateError("Every own subnet must be inside the selected VNet.")
    if any(first.overlaps(second) for i, first in enumerate(children) for second in children[i + 1:]):
        raise GateError("Primary subnet allocations overlap.")
    if ranges["vpnClientPool"].overlaps(ranges["vnet"]) or not 24 <= ranges["vpnClientPool"].prefixlen <= 29:
        raise GateError("The bounded P2S pool must be /24 through /29 and separate from the VNet.")
    for reserved in ("169.254.0.0/16", "172.30.0.0/16", "172.31.0.0/16", "192.0.2.0/24",
                     "100.100.0.0/17", "100.100.128.0/19", "100.100.160.0/19", "100.100.192.0/19"):
        if any(ranges[key].overlaps(ipaddress.ip_network(reserved)) for key in ("vnet", "vpnClientPool")):
            raise GateError("Own VNet and routed P2S ranges must avoid the documented ACA/AKS reserved ranges.")
    if ranges["firewall"].prefixlen > 26 or ranges["gateway"].prefixlen > 27:
        raise GateError("AzureFirewallSubnet requires /26 or larger and GatewaySubnet /27 or larger.")
    zones = network["zones"]
    if (not isinstance(zones, list) or not zones or any(zone not in ("1", "2", "3") for zone in zones)
            or len(zones) != len(set(zones))):
        raise GateError("Supply distinct explicit zone candidates; this does not prove regional capacity.")
    users = manifest.get("vpnUserObjectIds")
    if (not isinstance(users, list) or len(users) != 4 or any(not isinstance(user, str) for user in users)
            or len(set(users)) != 4):
        raise GateError("The VPN audience must bind exactly the four explicitly selected existing users.")
    for user in users:
        require_guid(user, "selected VPN user")
    if not {actor["objectId"] for actor in manifest.get("actors", [])}.issubset(users):
        raise GateError("The selected business actors and the four-user VPN selection differ.")
    normalized_policy(manifest)


def normalized_policy(manifest, network_phase=None, platform=None, *, allow_clients=False):
    selected = manifest.get("edgePolicy")
    if (not isinstance(selected, dict) or set(selected) != {"dnsDomains", "httpsRules"}
            or not isinstance(selected["dnsDomains"], list)
            or not isinstance(selected["httpsRules"], list)):
        raise GateError("An explicit reviewed managed-edge DNS and HTTPS policy is required.")
    domains = {hostname(value) for value in selected["dnsDomains"]}
    rules, names = [], set()
    for rule in selected["httpsRules"]:
        if (not isinstance(rule, dict) or set(rule) != {"name", "sourceSegments", "targetFqdns"}
                or not isinstance(rule["name"], str)
                or not re.fullmatch(r"[a-z0-9][a-z0-9-]{0,78}[a-z0-9]", rule["name"])
                or rule["name"] in names or rule["name"] == "vpn-private-api"):
            raise GateError("Managed HTTPS rules require distinct controlled names and exact fields.")
        segments, targets = rule["sourceSegments"], rule["targetFqdns"]
        if (not isinstance(segments, list) or not segments
                or any(segment not in SOURCE_SEGMENTS for segment in segments)
                or len(segments) != len(set(segments))
                or not isinstance(targets, list) or not targets):
            raise GateError("HTTPS rules must explicitly name own source segments and reviewed FQDNs.")
        names.add(rule["name"])
        targets = sorted({hostname(value, wildcard=True) for value in targets})
        rules.append({"name": rule["name"],
                      "sourceAddresses": [manifest["network"][segment] for segment in segments],
                      "targetFqdns": targets})
    private = set()
    if network_phase:
        for value in network_phase["network"]["dns"].values():
            for zone_id in value if isinstance(value, list) else [value]:
                if not isinstance(zone_id, str) or "/privatednszones/" not in zone_id.lower():
                    raise GateError("Private DNS extensions require actual own zone resource bindings.")
                domains.add(hostname(zone_id.rsplit("/", 1)[-1]))
    if platform:
        private.update(endpoint_host(value) for value in platform["endpoints"].values())
        services = platform.get("privateServiceEndpoints")
        if (not isinstance(services, dict) or set(services) != {"registryData", "telemetry"}
                or any(not isinstance(values, list) or not values for values in services.values())):
            raise GateError("Actual ACR data and telemetry endpoint bindings are required; no public wildcard fallback.")
        private.update(endpoint_host(value) for values in services.values() for value in values)
        domains.update(private)
        domain = platform.get("containerEnvironmentDomain")
        if not domain:
            raise GateError("The actual internal Container Apps environment DNS domain is required.")
        domains.add(hostname(domain))
        for role, value in platform.get("runtimeUrls", {}).items():
            host = endpoint_host(value)
            domains.add(host)
            if role in ("workflows", "operations"):
                private.add(host)
        hosts = platform.get("runtimePrivateHostNames")
        if (not isinstance(hosts, dict) or set(hosts) not in (set(), {"workflows", "operations"})
                or any(not isinstance(values, list) or not values for values in hosts.values())):
            raise GateError("Actual Functions hostname bindings are required; never synthesize SCM aliases.")
        private.update(hostname(value) for values in hosts.values() for value in values)
        domains.update(private)
    if allow_clients:
        url = (platform or {}).get("runtimeUrls", {}).get("api")
        host = endpoint_host(url)
        domain = hostname(platform["containerEnvironmentDomain"])
        if not host.endswith("." + domain):
            raise GateError("P2S HTTPS must target the actual own internal API/UI, not an arbitrary host.")
        rules.append({"name": "vpn-private-api",
                      "sourceAddresses": [manifest["network"]["vpnClientPool"]],
                      "targetFqdns": [host]})
    return {"dnsDomains": sorted(domains), "httpsRules": rules,
            "privateOnlyFqdns": sorted(private),
            "sourcePrefixes": [manifest["network"][key] for key in SOURCE_SEGMENTS]
                              + [manifest["network"]["vpnClientPool"]]}


def publishing_host(platform, role):
    if role not in ("workflows", "operations"):
        raise GateError("Only the two own Functions roles have a private publishing endpoint.")
    bindings = platform.get("runtimePrivateHostNames")
    hosts = bindings.get(role) if isinstance(bindings, dict) else None
    if not isinstance(hosts, list) or not hosts:
        raise GateError("Actual role-specific Functions hostnames are missing; no SCM-name construction.")
    names = [hostname(value) for value in hosts]
    site = endpoint_host(platform.get("runtimeUrls", {}).get(role))
    candidates = [value for value in names if ".scm." in value and value.endswith(".azurewebsites.net")]
    if site not in names or len(candidates) != 1 or site == candidates[0]:
        raise GateError("One actual provider-returned SCM host and its own site binding are required.")
    return candidates[0]


def vpn_audience_body(manifest, scope_id):
    return {
        "requestedAccessTokenVersion": 1,
        "oauth2PermissionScopes": [{
            "id": scope_id, "value": "p2s_access", "type": "Admin", "isEnabled": True,
            "adminConsentDisplayName": "Connect to the own synthetic primary VPN",
            "adminConsentDescription": "Network access only; no PMS, directory or Azure administrative role.",
        }],
        "preAuthorizedApplications": [{"appId": VPN_CLIENT, "delegatedPermissionIds": [scope_id]}],
    }


def verify_vpn_identity(client, manifest, identity, scope_id):
    app_id, sp_id = identity["objectId"], identity["servicePrincipalObjectId"]
    app = client.get(GRAPH + "/v1.0/applications/" + app_id
                     + "?$select=id,appId,signInAudience,identifierUris,api,appRoles,requiredResourceAccess,"
                       "passwordCredentials,keyCredentials")
    principal = client.get(GRAPH + "/v1.0/servicePrincipals/" + sp_id
                           + "?$select=id,appId,appOwnerOrganizationId,appRoleAssignmentRequired,accountEnabled")
    if (app.get("id") != app_id or app.get("appId") != identity["clientId"]
            or app.get("signInAudience") != "AzureADMyOrg"
            or app.get("identifierUris") != ["api://" + identity["clientId"]]
            or any(app.get(key) != [] for key in ("appRoles", "requiredResourceAccess", "passwordCredentials",
                                                "keyCredentials"))
            or principal.get("id") != sp_id or principal.get("appId") != identity["clientId"]
            or principal.get("appOwnerOrganizationId") != manifest["binding"]["tenantId"]
            or principal.get("appRoleAssignmentRequired") is not True
            or principal.get("accountEnabled") is not True):
        raise GateError("The own VPN audience/SP readback differs; no tenant-wide or substituted audience.")
    actual, expected = app.get("api", {}), vpn_audience_body(manifest, scope_id)
    if (actual.get("requestedAccessTokenVersion") != 1
            or actual.get("preAuthorizedApplications") != expected["preAuthorizedApplications"]):
        raise GateError("The VPN token/client preauthorization contract changed.")
    scopes = actual.get("oauth2PermissionScopes", [])
    if (len(scopes) != 1 or any(scopes[0].get(key) != value
                              for key, value in expected["oauth2PermissionScopes"][0].items())):
        raise GateError("The own admin-consent-only VPN scope is not intact.")
    assignments = client.collection(GRAPH + "/v1.0/servicePrincipals/" + sp_id
                                    + "/appRoleAssignedTo?$select=principalId,principalType,appRoleId,resourceId")
    if (len(assignments) != 4
            or {row.get("principalId") for row in assignments} != set(manifest["vpnUserObjectIds"])
            or any(row.get("principalType") != "User" or row.get("resourceId") != sp_id
                   or row.get("appRoleId") != DEFAULT_APP_ROLE for row in assignments)):
        raise GateError("VPN assignments must be exactly the four selected users and default network access.")
    return {"applicationClientId": identity["clientId"], "servicePrincipalObjectId": sp_id,
            "assignmentRequired": True, "assignedUserCount": 4,
            "tokenAuthenticationProven": False, "businessOrProductRightsChanged": False}


def read_owned(client, manifest, identifier, resource_type, version=NETWORK_VERSION, *, policy=False):
    from primary_bootstrap import assert_owned_id
    if (not isinstance(identifier, str) or any(value in identifier for value in ("?", "#", "%", "\\"))
            or any(part in (".", "..") for part in identifier.split("/"))):
        raise GateError("Invalid owned resource reference.")
    assert_owned_id(identifier, manifest)
    value = client.get(ARM + identifier + "?api-version=" + version)
    if (str(value.get("id", "")).lower() != identifier.lower()
            or str(value.get("type", "")).lower() != resource_type.lower()
            or str(value.get("location", "")).lower() != manifest["location"]
            or value.get("properties", {}).get("provisioningState") != "Succeeded"):
        raise GateError("An owned network/compute resource lacks the exact successful provider readback.")
    binding = (value.get("tags") or {}).get("cohorsBinding")
    if binding != manifest["bindingHash"] and not (
            policy and resource_type == "Microsoft.Network/firewallPolicies" and binding is None):
        raise GateError("Resource ownership binding changed.")
    return value


def observe_edge(client, manifest, stage, *, bound, identity):
    network, edge = stage["network"], stage["edge"]
    ids = edge["resources"]
    firewall = read_owned(client, manifest, ids["firewallId"], "Microsoft.Network/azureFirewalls")
    gateway = read_owned(client, manifest, ids["gatewayId"], "Microsoft.Network/virtualNetworkGateways")
    vnet = read_owned(client, manifest, network["id"], "Microsoft.Network/virtualNetworks")
    policy = read_owned(client, manifest, ids["firewallPolicyId"], "Microsoft.Network/firewallPolicies",
                        policy=True)
    for key in ("firewallPublicIpId", "gatewayPublicIpId"):
        public_ip = read_owned(client, manifest, ids[key], "Microsoft.Network/publicIPAddresses")
        if (public_ip.get("sku") != {"name": "Standard", "tier": "Regional"}
                or sorted(public_ip.get("zones", [])) != sorted(manifest["network"]["zones"])
                or public_ip["properties"].get("publicIPAllocationMethod") != "Static"
                or public_ip["properties"].get("publicIPAddressVersion") != "IPv4"):
            raise GateError("An own edge public IP differs from the selected Standard/static/IPv4/zone binding.")
    if sorted(firewall.get("zones", [])) != sorted(manifest["network"]["zones"]):
        raise GateError("The actual Firewall zones differ from the separately reviewed candidate.")
    fw = firewall["properties"]
    if fw.get("autoscaleConfiguration"):
        raise GateError("Unreviewed Firewall prescaling is present; omitting it from a template does not reset its cost.")
    configs = fw.get("ipConfigurations", [])
    if (fw.get("sku") != {"name": "AZFW_VNet", "tier": "Standard"} or len(configs) != 1
            or fw.get("firewallPolicy", {}).get("id", "").lower() != ids["firewallPolicyId"].lower()
            or configs[0].get("properties", {}).get("publicIPAddress", {}).get("id", "").lower()
            != ids["firewallPublicIpId"].lower()):
        raise GateError("The Firewall SKU, own policy or public-IP binding changed.")
    actual_ip = configs[0]["properties"].get("privateIPAddress")
    try:
        if not isinstance(actual_ip, str):
            raise ValueError()
        address = ipaddress.ip_address(actual_ip)
    except ValueError as error:
        raise GateError("The provider did not return an actual Firewall IPv4 address.") from error
    if (address.version != 4 or address not in ipaddress.ip_network(manifest["network"]["firewall"])
            or actual_ip != edge.get("firewallPrivateIp")):
        raise GateError("Firewall IP differs from the preceding actual binding; never guess or silently refresh it.")
    settings = policy["properties"]
    if (settings.get("sku", {}).get("tier") != "Standard" or settings.get("threatIntelMode") != "Deny"
            or settings.get("dnsSettings", {}).get("enableProxy") is not True
            or settings.get("dnsSettings", {}).get("requireProxyForNetworkRules") is not True
            or settings.get("dnsSettings", {}).get("servers")):
        raise GateError("The Firewall DNS/threat policy changed; no alternate resolver or public fallback.")
    properties = vnet["properties"]
    if properties.get("addressSpace", {}).get("addressPrefixes") != [manifest["network"]["vnet"]]:
        raise GateError("The actual VNet address space differs from the reviewed private allocation.")
    if properties.get("virtualNetworkPeerings"):
        raise GateError("No shared hub, Fabric peering or unreviewed VNet peering is selected.")
    if properties.get("dhcpOptions", {}).get("dnsServers", []) != ([actual_ip] if bound else []):
        raise GateError("VNet DNS does not match the current staged Firewall binding.")
    subnets = {row["name"]: row["properties"] for row in properties.get("subnets", [])}
    expected_subnets = {
        "containerApps": "snet-container-apps", "workflows": "snet-workflows",
        "operations": "snet-operations", "privateEndpoints": "snet-private-endpoints",
        "operator": "snet-operator", "client": "snet-test-client", "bastion": "AzureBastionSubnet",
        "firewall": "AzureFirewallSubnet", "gateway": "GatewaySubnet",
    }
    if set(subnets) != set(expected_subnets.values()):
        raise GateError("The actual own subnet set differs; no parallel NVA or unreviewed subnet.")
    for key, name in expected_subnets.items():
        if subnets[name].get("addressPrefix") != manifest["network"][key]:
            raise GateError("An actual subnet differs from its reviewed CIDR.")
    for key in ("containerApps", "workflows", "operations", "operator", "client", "gateway"):
        expected = network["gatewayRouteTableId" if key == "gateway" else "routeTableId"]
        if subnets[expected_subnets[key]].get("routeTable", {}).get("id", "").lower() != expected.lower():
            raise GateError("A required route table exists but is not associated with its actual subnet.")
    if subnets["AzureFirewallSubnet"].get("routeTable"):
        raise GateError("No unreviewed forced-tunnel or return-route override may be attached to the Firewall.")
    for name in ("AzureFirewallSubnet", "GatewaySubnet"):
        if name not in subnets or subnets[name].get("networkSecurityGroup"):
            raise GateError("The dedicated Firewall/Gateway subnets must exist without NSGs.")
    if "snet-egress" in subnets:
        raise GateError("A parallel legacy NVA subnet is not part of this primary profile.")
    for key, propagation, prefixes in (
            ("routeTableId", True, ["0.0.0.0/0"] + ([manifest["network"]["vpnClientPool"]] if bound else [])),
            ("gatewayRouteTableId", False, [manifest["network"][name] for name in
                                           ("containerApps", "workflows", "operations", "privateEndpoints",
                                            "operator", "client", "bastion")])):
        routes = read_owned(client, manifest, network[key], "Microsoft.Network/routeTables")["properties"]
        actual = routes.get("routes", [])
        if (routes.get("disableBgpRoutePropagation") is not propagation or len(actual) != len(prefixes)
                or {row.get("properties", {}).get("addressPrefix") for row in actual} != set(prefixes)):
            raise GateError("The exact own default/return/GatewaySubnet route set changed.")
        for row in actual:
            route = row["properties"]
            if (route.get("nextHopType") != ("VirtualAppliance" if bound else "None")
                    or (route.get("nextHopIpAddress") or "") != (actual_ip if bound else "")):
                raise GateError("A route bypasses the selected Firewall or the initial blackhole.")
    gw = gateway["properties"]
    if (gw.get("gatewayType") != "Vpn" or gw.get("vpnType") != "RouteBased"
            or gw.get("vpnGatewayGeneration") != "Generation1"
            or gw.get("sku", {}).get("name") != "VpnGw1AZ" or gw.get("sku", {}).get("tier") != "VpnGw1AZ"
            or gw.get("activeActive") is not False or gw.get("enableBgp") is not False):
        raise GateError("The selected VPN SKU, generation or active-standby topology changed.")
    gateway_configs = gw.get("ipConfigurations", [])
    if (len(gateway_configs) != 1
            or gateway_configs[0].get("properties", {}).get("publicIPAddress", {}).get("id", "").lower()
            != ids["gatewayPublicIpId"].lower()
            or gateway_configs[0].get("properties", {}).get("subnet", {}).get("id", "").lower()
            != (network["id"] + "/subnets/GatewaySubnet").lower()
            or configs[0].get("properties", {}).get("subnet", {}).get("id", "").lower()
            != (network["id"] + "/subnets/AzureFirewallSubnet").lower()):
        raise GateError("The actual gateway/Firewall interface binding differs from the own subnet/public IP.")
    client_configuration = gw.get("vpnClientConfiguration")
    if not bound:
        if client_configuration:
            raise GateError("P2S authentication must remain unconfigured before managed-edge binding.")
    else:
        configuration = client_configuration or {}
        tenant = manifest["binding"]["tenantId"]
        if (configuration.get("aadTenant", "").rstrip("/") != "https://login.microsoftonline.com/" + tenant
                or configuration.get("aadIssuer") != "https://sts.windows.net/" + tenant + "/"
                or configuration.get("aadAudience") != identity["clientId"]
                or configuration.get("vpnAuthenticationTypes") != ["AAD"]
                or configuration.get("vpnClientProtocols") != ["OpenVPN"]
                or configuration.get("vpnClientAddressPool", {}).get("addressPrefixes")
                != [manifest["network"]["vpnClientPool"]]):
            raise GateError("P2S must use this own single-tenant audience, OpenVPN and the reviewed pool only.")
    return {"firewallPrivateIp": actual_ip, "stagedMetadataMatched": True,
            "dnsAndTrafficProven": False, "vpnAuthenticationConfigured": bound,
            "vpnLoginProven": False, "clientProfileGenerated": False, "clientInstalledOrConnected": False}


def observe_container_profiles(client, manifest, stage, *, runtime):
    platform = stage["platform"]
    identifier = platform["resources"]["containerEnvironment"]
    environment = read_owned(client, manifest, identifier, "Microsoft.App/managedEnvironments", APP_VERSION)
    properties = environment["properties"]
    if (properties.get("vnetConfiguration", {}).get("infrastructureSubnetId", "").lower()
            != (platform["resources"]["vnet"] + "/subnets/snet-container-apps").lower()
            or properties.get("defaultDomain") != platform.get("containerEnvironmentDomain")):
        raise GateError("The actual Container Apps network/domain differs from the generated own binding.")
    profiles = properties.get("workloadProfiles", [])
    selected = {row.get("name"): row for row in profiles}
    if (len(profiles) != 2 or set(selected) != {"Consumption", "D4"}
            or selected["Consumption"].get("workloadProfileType") != "Consumption"
            or selected["D4"].get("workloadProfileType") != "D4"
            or any(type(selected["D4"].get(key)) is not int or selected["D4"][key] != 1
                   for key in ("minimumCount", "maximumCount"))
            or properties.get("publicNetworkAccess") != "Disabled"
            or properties.get("vnetConfiguration", {}).get("internal") is not True):
        raise GateError("Expected the private D4/min=max1 environment with its mandatory unused Consumption profile.")
    states = client.collection(ARM + identifier + "/workloadProfileStates?api-version=" + APP_VERSION)
    state = [row for row in states if row.get("name") == "D4"]
    if (len(state) != 1 or any(type(state[0].get("properties", {}).get(key)) is not int
                               or state[0]["properties"][key] != 1
                               for key in ("minimumCount", "maximumCount", "currentCount"))):
        raise GateError("An actual single D4 node is not observed; configuration is not allocated capacity.")
    group = identifier.split("/providers/")[0]
    apps = client.collection(ARM + group + "/providers/Microsoft.App/containerApps?api-version=" + APP_VERSION)
    expected = ({role: platform["runtimeResources"][role].lower() for role in ("api", "agents")}
                if runtime else {})
    if {str(app.get("id", "")).lower() for app in apps} != set(expected.values()):
        raise GateError("Unexpected or missing Container Apps; unused Consumption cannot be inferred.")
    for app in apps:
        actual = app.get("properties", {})
        if (app.get("tags", {}).get("cohorsBinding") != manifest["bindingHash"]
                or str(actual.get("environmentId", "")).lower() != identifier.lower()
                or actual.get("workloadProfileName") != "D4"):
            raise GateError("Both own applications must explicitly use D4; a default or Consumption is refused.")
        template, configuration = actual.get("template", {}), actual.get("configuration", {})
        scale = template.get("scale", {})
        if (any(type(scale.get(key)) is not int or scale[key] != 1 for key in ("minReplicas", "maxReplicas"))
                or configuration.get("activeRevisionsMode") != "Single"):
            raise GateError("The single-node primary requires min=max1 per app and unchanged Single revision readiness.")
        containers = template.get("containers", [])
        if (not isinstance(containers, list) or len(containers) != 1 or template.get("initContainers")
                or (configuration.get("dapr") or {}).get("enabled")):
            raise GateError("Unreviewed additional application containers invalidate the single-node resource envelope.")
        resources = containers[0].get("resources", {})
        if resources.get("cpu") != 0.5 or resources.get("memory") not in ("1Gi", "1.0Gi"):
            raise GateError("The actual application resource request differs from the reviewed0.5vCPU/1GiB host.")
    return {"configuredProfiles": ["Consumption", "D4"], "selectedProfile": "D4",
            "observedD4Nodes": 1, "applicationCount": len(apps),
            "bothApplicationsObservedOnD4": runtime and len(apps) == 2,
            "bothApplicationsObservedAtSingleReplicaBounds": runtime and len(apps) == 2,
            "consumptionUnusedByApplications": True, "rolloutCapacityProven": False,
            "dnsAndHttpsProven": False, "haOrSloProven": False}


def observe_policy(client, manifest, stage, selected, *, bound, deny_transitive=False):
    ids = stage["edge"]["resources"]
    version = "2025-05-01"
    read_owned(client, manifest, ids["dnsPolicyId"], "Microsoft.Network/dnsResolverPolicies", version)
    for key, domains in (("deniedDomainsId", ["."]), ("allowedDomainsId", selected["dnsDomains"])):
        if not domains:
            if ids.get(key):
                raise GateError("An unexpected DNS allow list is present.")
            continue
        value = read_owned(client, manifest, ids[key], "Microsoft.Network/dnsResolverDomainLists", version)
        if sorted(value["properties"].get("domains", [])) != sorted(domains):
            raise GateError("The actual DNS domain list differs from the reviewed bound policy.")
    rules = client.collection(ARM + ids["dnsPolicyId"] + "/dnsSecurityRules?api-version=" + version)
    expected_rules = {ids["denyDnsRuleId"].lower(): ("Block", 65000, ids["deniedDomainsId"])}
    if selected["dnsDomains"]:
        expected_rules[ids["allowDnsRuleId"].lower()] = ("Allow", 100, ids["allowedDomainsId"])
    if len(rules) != len(expected_rules) or {row.get("id", "").lower() for row in rules} != set(expected_rules):
        raise GateError("Extra or missing DNS security rules prevent policy acceptance.")
    for row in rules:
        action, priority, domain_list = expected_rules[row["id"].lower()]
        properties = row.get("properties", {})
        if (properties.get("provisioningState") != "Succeeded"
                or properties.get("dnsSecurityRuleState") != "Enabled"
                or properties.get("action") != {"actionType": action} or properties.get("priority") != priority
                or properties.get("dnsResolverDomainLists") != [{"id": domain_list}]):
            raise GateError("DNS action, priority or domain-list binding changed.")
    links = client.collection(ARM + ids["dnsPolicyId"] + "/virtualNetworkLinks?api-version=" + version)
    if (len(links) != (1 if bound else 0)
            or bound and (links[0].get("id", "").lower() != ids["dnsLinkId"].lower()
                          or links[0].get("properties", {}).get("provisioningState") != "Succeeded"
                          or links[0]["properties"].get("virtualNetwork", {}).get("id", "").lower()
                          != stage["network"]["id"].lower())):
        raise GateError("DNS policy linkage is not exactly the staged own VNet; no shared/Fabric link.")
    private_rules = [] if not selected["privateOnlyFqdns"] else [{
        "name": "private-data-stays-private", "ruleType": "NetworkRule", "ipProtocols": ["TCP"],
        "sourceAddresses": selected["sourcePrefixes"], "destinationFqdns": selected["privateOnlyFqdns"],
        "destinationPorts": ["443", "1433"],
    }]
    https_rules = [{**rule, "ruleType": "ApplicationRule",
                    "protocols": [{"protocolType": "Https", "port": 443}], "terminateTLS": False}
                   for rule in selected["httpsRules"]]
    groups = client.collection(ARM + ids["firewallPolicyId"] + "/ruleCollectionGroups?api-version=" + NETWORK_VERSION)
    private_collections = []
    if deny_transitive:
        private_collections.append(("deny-spoke-transit", 100, "Deny", [{
            "name": "no-implicit-private-transit", "ruleType": "NetworkRule", "ipProtocols": ["Any"],
            "sourceAddresses": selected["sourcePrefixes"],
            "destinationAddresses": ["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16"], "destinationPorts": ["*"],
        }]))
    if private_rules:
        private_collections.append(("deny-private-data-egress", 110 if deny_transitive else 100, "Deny", private_rules))
    expected_groups = {
        ids["privateDataRulesId"].lower(): (100, private_collections),
        ids["httpsRulesId"].lower(): (200, [("allow-reviewed-https", 200, "Allow", https_rules)] if https_rules else []),
    }
    if len(groups) != 2 or {row.get("id", "").lower() for row in groups} != set(expected_groups):
        raise GateError("Unexpected Firewall rule collections prevent policy acceptance.")
    for group in groups:
        group_priority, expected_collections = expected_groups[group["id"].lower()]
        properties = group.get("properties", {})
        collections = properties.get("ruleCollections", [])
        if (properties.get("provisioningState") != "Succeeded" or properties.get("priority") != group_priority
                or len(collections) != len(expected_collections)
                or {item.get("name") for item in collections} != {item[0] for item in expected_collections}):
            raise GateError("The actual Firewall group priority or collection count changed.")
        for name, priority, action, expected in expected_collections:
            collection = next(item for item in collections if item["name"] == name)
            if (collection.get("priority") != priority or collection.get("action") != {"type": action}
                    or collection.get("ruleCollectionType") != "FirewallPolicyFilterRuleCollection"):
                raise GateError("The Firewall rule action/type changed.")
            actual = collection.get("rules", [])
            expected_by_name = {rule["name"]: rule for rule in expected}
            if len(actual) != len(expected) or {rule.get("name") for rule in actual} != set(expected_by_name):
                raise GateError("Missing or injected Firewall rules prevent policy acceptance.")
            for rule in actual:
                expected_rule = expected_by_name[rule["name"]]
                for key, value in rule.items():
                    if key not in expected_rule and value not in (None, [], {}, "", False):
                        raise GateError("An unexpected nonempty Firewall rule selector could widen the reviewed scope.")
                for key, value in expected_rule.items():
                    observed = rule.get(key)
                    if isinstance(value, list) and all(isinstance(item, str) for item in value):
                        equal = isinstance(observed, list) and sorted(observed) == sorted(value)
                    else:
                        equal = observed == value
                    if not equal:
                        raise GateError("Actual Firewall sources, destinations or HTTPS-only protocols differ.")
    return {"policyMetadataMatched": True, "dnsDomainCount": len(selected["dnsDomains"]),
            "httpsRuleCount": len(https_rules), "privateOnlyHostCount": len(selected["privateOnlyFqdns"]),
            "dnsAndHttpsTrafficProven": False, "exactQnameOrPerSourceDnsIsolationClaim": False}


def observe_edge_monitoring(client, manifest, network_stage, platform_stage):
    workspace = platform_stage["platform"]["resources"]["workspace"]
    read_owned(client, manifest, workspace, "Microsoft.OperationalInsights/workspaces", "2025-02-01")
    settings = platform_stage.get("edgeMonitoring")
    if not isinstance(settings, dict) or set(settings) != {"firewall", "gateway"}:
        raise GateError("The own edge metric settings are not bound to the completed foundation.")
    for role, setting_id in settings.items():
        target = network_stage["edge"]["resources"][role + "Id"]
        expected = target + "/providers/Microsoft.Insights/diagnosticSettings/metrics-only"
        if not isinstance(setting_id, str) or setting_id.lower() != expected.lower():
            raise GateError("The metric setting does not target the actual own Firewall/Gateway.")
        values = client.collection(ARM + target
                                   + "/providers/Microsoft.Insights/diagnosticSettings?api-version=2021-05-01-preview")
        if len(values) != 1 or str(values[0].get("id", "")).lower() != expected.lower():
            raise GateError("Unexpected or missing edge diagnostic settings; no unreviewed parallel log sink.")
        properties = values[0].get("properties", {})
        if (str(properties.get("workspaceId", "")).lower() != workspace.lower()
                or any(properties.get(key) for key in (
                    "storageAccountId", "eventHubAuthorizationRuleId", "eventHubName", "marketplacePartnerId"))):
            raise GateError("Edge metrics must go only to the actual private workload workspace.")
        logs, metrics = properties.get("logs", []), properties.get("metrics", [])
        if (not isinstance(logs, list) or any(row.get("enabled") is not False for row in logs)
                or not isinstance(metrics, list) or len(metrics) != 1
                or metrics[0].get("category") != "AllMetrics" or metrics[0].get("enabled") is not True):
            raise GateError("Only supported metric export is selected; traffic/DNS/VPN logs remain unreviewed.")
    return {"metricSettingsMatched": True, "resourceLogCategoriesEnabled": 0,
            "metricIngestionObserved": False, "networkAlertDeliveryProven": False}
