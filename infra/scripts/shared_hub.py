"""Versioned shared-network contracts. Metadata receipts never accept live traffic."""

import copy
import datetime as dt
import ipaddress
import re
import urllib.parse

from primary_control import ARM, GateError, digest, parse_time, require_guid, utcnow
from primary_network import hostname


HUB_CONTRACT = "cohors-shared-hub-bootstrap-1"
PRIMARY_CONTRACT = "cohors-primary-bootstrap-3"
RECEIPT_CONTRACT = "cohors-shared-hub-consumer-1"
BINDINGS_CONTRACT = "cohors-shared-hub-bindings-1"
CONSUMERS = ("primary", "fabric")
STATES = ("reserved", "peered", "connected", "data-bound", "web-bound", "offboarded")
HUB_SUBNETS = ("firewall", "gateway", "waf", "endpoints")
PRIMARY_SUBNETS = ("containerApps", "workflows", "operations", "privateEndpoints", "operator", "client", "bastion")
PRIMARY_SUBNET_NAMES = dict(zip(PRIMARY_SUBNETS, ("snet-container-apps", "snet-workflows", "snet-operations",
                                                "snet-private-endpoints", "snet-operator", "snet-test-client",
                                                "AzureBastionSubnet")))
FABRIC_SUBNETS = ("client", "agents", "endpoints", "operatorEndpoints")
NETWORK_API = "2024-07-01"
WAF_API = "2025-03-01"
DNS_API = "2020-06-01"
POLICY_API = "2025-05-01"
PRIVATE_RANGES = tuple(ipaddress.ip_network(value) for value in ("10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16"))
OWNERSHIP = {
    "hubLifecycleOwner": "hub", "consumerMayCreateHub": False, "consumerMayDeleteHub": False,
    "consumerMayChangeSharedDirectory": False, "consumerMayChangeHubDns": False,
    "offboarding": "stop-own-work-then-owner-releases-exact-consumer-references",
    "sharedFailureDomain": True,
}


def exact(value, fields, label):
    if not isinstance(value, dict) or set(value) != set(fields):
        raise GateError(f"{label} requires exactly the versioned fields; unknown/null contracts are not accepted.")
    return value


def sha(value, label="evidence hash"):
    if not isinstance(value, str) or not re.fullmatch(r"[0-9a-f]{64}", value):
        raise GateError(f"A real bound {label} is required.")
    return value


def arm_id(value, *, scope=None, kind=None):
    if (not isinstance(value, str) or not re.fullmatch(
            r"/subscriptions/[0-9a-f-]{36}/resourceGroups/[A-Za-z0-9_.()-]+(?:/providers/[A-Za-z0-9._/-]+)?", value)
            or any(part in (".", "..", "") for part in value.split("/")[1:])):
        raise GateError("An exact canonical ARM reference is required; URLs, queries and traversal are forbidden.")
    if scope and not (value.lower() == scope.lower() or value.lower().startswith(scope.lower() + "/")):
        raise GateError("The reference is outside its explicitly bound owner scope.")
    require_guid(value.split("/")[2], "ARM subscription")
    if kind:
        suffix = value.split("/providers/", 1)[-1].split("/")
        actual = "/".join([suffix[0], *suffix[1::2]]) if len(suffix) > 1 else ""
        if actual.lower() != kind.lower():
            raise GateError("The reference has a different ARM resource type.")
    return value


def cidr(value):
    try:
        network = ipaddress.ip_network(value, strict=True) if isinstance(value, str) else None
    except ValueError as error:
        raise GateError("An allocated canonical RFC1918 IPv4 CIDR is required.") from error
    if not network or network.version != 4 or not any(network.subnet_of(block) for block in PRIVATE_RANGES):
        raise GateError("An allocated canonical RFC1918 IPv4 CIDR is required; null/examples are not an allocation.")
    return network


def private_ip(value, prefix):
    try:
        address = ipaddress.ip_address(value) if isinstance(value, str) else None
    except ValueError as error:
        raise GateError("An observed private IPv4 address is required.") from error
    network = cidr(prefix)
    if not address or address.version != 4 or address not in network or int(address) - int(network.network_address) < 4 or address == network.broadcast_address:
        raise GateError("The address is outside the bound subnet or is Azure-reserved.")
    return value


def nonoverlap(values):
    networks = [cidr(value) for value in values]
    if any(first.overlaps(second) for i, first in enumerate(networks) for second in networks[i + 1:]):
        raise GateError("Hub, spokes, subnets and P2S allocations must not overlap.")


def hub_group(manifest):
    return f"/subscriptions/{manifest['binding']['subscriptionId']}/resourceGroups/{manifest['names']['hubResourceGroup']}"


def validate_hub_manifest(manifest):
    if manifest.get("generation") != manifest["deploymentId"]:
        raise GateError("A new hub generation must be its own deploymentId; legacy networks cannot be adopted.")
    if manifest["names"] != {"hubResourceGroup": f"rg-{manifest['namePrefix']}-hub"}:
        raise GateError("The hub has exactly one separately generated, hub-owned resource group.")
    if manifest.get("consumerSlots") != list(CONSUMERS):
        raise GateError("The shared pilot has exactly the Primary and Fabric consumer slots.")
    network = exact(manifest.get("network"), ("vnet", "vpnClientPool", "zones", *HUB_SUBNETS), "Hub network")
    parent = cidr(network["vnet"])
    nonoverlap([network[key] for key in HUB_SUBNETS])
    nonoverlap([network["vnet"], network["vpnClientPool"]])
    for role, largest_prefix in (("firewall", 26), ("gateway", 27), ("waf", 24), ("endpoints", 27)):
        child = cidr(network[role])
        if not child.subnet_of(parent) or child.prefixlen > largest_prefix:
            raise GateError("Hub subnets require separate provider-sized allocations (/26 Firewall, /27 gateway/PE, /24 WAF candidate).")
    if not 24 <= cidr(network["vpnClientPool"]).prefixlen <= 29:
        raise GateError("The pilot P2S pool is bounded to /24 through /29, not preallocated by a template.")
    zones = network["zones"]
    if not isinstance(zones, list) or not zones or len(set(zones)) != len(zones) or not set(zones) <= {"1", "2", "3"}:
        raise GateError("Explicit distinct regional zone candidates are required, not a capacity claim.")
    users = manifest.get("vpnUserObjectIds")
    if not isinstance(users, list) or len(users) != 4 or len(set(users)) != 4:
        raise GateError("Reuse exactly the four already resolved users, without a new directory search.")
    for user in users:
        require_guid(user, "resolved VPN user")
    sha(manifest.get("resolvedUsersEvidenceSha256"), "original four-user resolution evidence")
    waf = exact(manifest.get("waf"), ("sku", "frontendPrivateIp", "instanceCount"), "Private WAF profile")
    if waf["sku"] != "WAF_v2" or type(waf["instanceCount"]) is not int or not 1 <= waf["instanceCount"] <= 125:
        raise GateError("WAF_v2 needs an explicitly reviewed instance count; no inferred capacity or budget increase.")
    private_ip(waf["frontendPrivateIp"], network["waf"])
    if not isinstance(manifest.get("dnsZones"), list) or not manifest["dnsZones"]:
        raise GateError("The hub-owned canonical DNS namespace list must be explicit.")
    if len(set(manifest["dnsZones"])) != len(manifest["dnsZones"]):
        raise GateError("A private DNS namespace must have exactly one hub-owned zone.")
    for zone in manifest["dnsZones"]:
        hostname(zone)
    if "privatelink.vaultcore.azure.net" not in manifest["dnsZones"]:
        raise GateError("The shared certificate vault requires its one hub-owned private DNS zone.")
    boundary = exact(manifest.get("pilotBoundary"), ("createdAtUtc", "expiresAtUtc", "budgetBindingSha256", "sourceEvidenceSha256"), "Original pilot boundary")
    if boundary["createdAtUtc"] != manifest["createdAtUtc"] or boundary["expiresAtUtc"] != manifest["expiresAtUtc"] or boundary["budgetBindingSha256"] != manifest.get("budgetBindingSha256"):
        raise GateError("Hub creation/transition cannot reset the original pilot clock or budget binding.")
    sha(boundary["sourceEvidenceSha256"], "original pilot boundary evidence")
    policy(manifest.get("edgePolicy"), {"hubWaf": network["waf"]})
    for field in ("addressReviewSha256", "capacityReviewSha256", "budgetBindingSha256"):
        sha(manifest.get(field), field)


def validate_primary_manifest(manifest):
    if manifest.get("containerAppsProfile") != "D4":
        raise GateError("The Primary shared-hub mode retains the D4 single-node candidate.")
    if any(key in manifest for key in ("azureFirewall", "vpn", "vpnUserObjectIds", "nvaPolicy")):
        raise GateError("A shared-hub consumer cannot retain an own gateway, VPN audience or NVA lifecycle.")
    network = exact(manifest.get("network"), ("vnet", *PRIMARY_SUBNETS), "Primary spoke network")
    parent = cidr(network["vnet"])
    nonoverlap([network[key] for key in PRIMARY_SUBNETS])
    for role in PRIMARY_SUBNETS:
        subnet = cidr(network[role])
        if not subnet.subnet_of(parent) or subnet.prefixlen > (26 if role in ("workflows", "operations", "bastion") else 27):
            raise GateError("Primary spoke subnets must have separate reviewed provider-sized allocations.")
    hub = exact(manifest.get("hubBinding"), ("generation", "bindingHash", "resourceGroupId", "infrastructureCommit"), "Hub owner binding")
    require_guid(hub["generation"], "hub generation")
    sha(hub["bindingHash"], "hub manifest hash")
    arm_id(hub["resourceGroupId"])
    if not re.fullmatch(r"[0-9a-f]{40}", hub["infrastructureCommit"] or ""):
        raise GateError("The exact hub controller commit is required.")
    if hub["bindingHash"] == manifest["bindingHash"]:
        raise GateError("Hub and consumer ownership bindings must be separate.")
    policy(manifest.get("edgePolicy"), {key: network[key] for key in PRIMARY_SUBNETS if key != "bastion"})


def policy(value, source_prefixes):
    selected = exact(value, ("dnsDomains", "httpsRules"), "Source-scoped egress policy")
    if not isinstance(selected["dnsDomains"], list) or not isinstance(selected["httpsRules"], list):
        raise GateError("DNS and HTTPS policy entries must be explicit lists.")
    domains = sorted({hostname(item) for item in selected["dnsDomains"]})
    rules, names = [], set()
    for item in selected["httpsRules"]:
        exact(item, ("name", "sourceSegments", "targetFqdns"), "HTTPS rule")
        if not isinstance(item["name"], str) or not re.fullmatch(r"[a-z][a-z0-9-]{1,60}", item["name"]) or item["name"] in names:
            raise GateError("HTTPS rules need unique bounded names.")
        if not isinstance(item["sourceSegments"], list) or not item["sourceSegments"] or not set(item["sourceSegments"]) <= set(source_prefixes):
            raise GateError("An HTTPS rule may reference only its own explicitly bound source subnets.")
        if not isinstance(item["targetFqdns"], list) or not item["targetFqdns"]:
            raise GateError("HTTPS targets must be explicit; no Internet catch-all.")
        names.add(item["name"])
        rules.append({"name": item["name"], "sourceAddresses": [source_prefixes[key] for key in item["sourceSegments"]],
                      "targetFqdns": sorted({hostname(value, wildcard=True) for value in item["targetFqdns"]})})
    return {"dnsDomains": domains, "httpsRules": rules}


def validate_spoke(value, consumer, manifest):
    exact(value, ("resourceGroupId", "vnetId", "location", "addressPrefix", "subnets"), "Spoke")
    group = arm_id(value["resourceGroupId"])
    if "/providers/" in group or group.lower() == hub_group(manifest).lower():
        raise GateError("A spoke must have a separate consumer-owned resource group.")
    arm_id(value["vnetId"], scope=group, kind="Microsoft.Network/virtualNetworks")
    parent = cidr(value["addressPrefix"])
    roles = PRIMARY_SUBNETS if consumer == "primary" else FABRIC_SUBNETS
    exact(value["subnets"], roles, "Spoke subnet roles")
    for role, subnet in value["subnets"].items():
        exact(subnet, ("id", "addressPrefix"), "Spoke subnet")
        arm_id(subnet["id"], scope=value["vnetId"], kind="Microsoft.Network/virtualNetworks/subnets")
        allocation = cidr(subnet["addressPrefix"])
        if not allocation.subnet_of(parent) or allocation.prefixlen > 27:
            raise GateError("Spoke subnets need actual nonoverlapping allocations; /27 is only a minimum contract, not hosted-provider acceptance.")
    nonoverlap([subnet["addressPrefix"] for subnet in value["subnets"].values()])
    if value["location"] not in ("francecentral", "swedencentral", "westeurope", "northeurope", "germanywestcentral"):
        raise GateError("A spoke must bind its reviewed EU region; no automatic regional move.")
    if consumer == "primary" and value["location"] != "francecentral":
        raise GateError("The Primary location has not been changed by the hub decision.")


def validate_web(value, consumer, spoke, manifest):
    exact(value, ("frontendHost", "frontendZone", "frontendRecord", "certificateSecretId",
                  "certificateEvidenceSha256", "backendFqdn", "backendResourceId", "backendPrivateIp",
                  "backendRegion", "backendKind", "backendRootCertificate", "probe", "limits",
                  "applicationContractSha256", "forwardedHeadersEvidenceSha256", "privateDnsEvidenceSha256"), "HTTPS backend")
    for key in ("frontendHost", "frontendZone", "backendFqdn"):
        hostname(value[key])
    if (value["frontendHost"] != value["frontendRecord"] + "." + value["frontendZone"]
            or not re.fullmatch(r"[a-z][a-z0-9-]{0,62}", value["frontendRecord"] or "")):
        raise GateError("Each private listener needs one exact frontend host and owned DNS record; no wildcard listener.")
    if value["backendRegion"] != manifest["location"] or spoke["location"] != manifest["location"]:
        raise GateError("Cross-region WAF backend/Key Vault private endpoint paths are unsupported here; no public fallback or silent move.")
    if value["backendKind"] not in ("ContainerAppInternal", "PrivateEndpointWeb"):
        raise GateError("Only a separately qualified human web backend is supported; not Fabric MCP or C1 Hosted.")
    arm_id(value["backendResourceId"], scope=spoke["resourceGroupId"])
    if value["backendKind"] == "ContainerAppInternal" and consumer != "primary":
        raise GateError("Fabric has no separate human Container Apps hosting contract; do not adopt C1 Hosted.")
    subnet_role = ("containerApps" if value["backendKind"] == "ContainerAppInternal"
                   else "privateEndpoints" if consumer == "primary" else "endpoints")
    private_ip(value["backendPrivateIp"], spoke["subnets"][subnet_role]["addressPrefix"])
    secret = urllib.parse.urlsplit(value["certificateSecretId"])
    if (secret.scheme != "https" or not secret.hostname or secret.netloc != secret.hostname
            or not secret.hostname.endswith(".vault.azure.net") or secret.query or secret.fragment
            or not re.fullmatch(r"/secrets/[A-Za-z0-9-]+/[0-9a-f]{32}", secret.path)):
        raise GateError("Use the observed versioned certificate secret in the independent hub vault, never inline PFX/passwords.")
    for key in ("certificateEvidenceSha256", "applicationContractSha256", "forwardedHeadersEvidenceSha256", "privateDnsEvidenceSha256"):
        sha(value[key], key)
    if value["backendRootCertificate"] is not None and not re.fullmatch(r"[A-Za-z0-9+/=\r\n]+", value["backendRootCertificate"] or ""):
        raise GateError("A private backend CA must use the reviewed public DER certificate, never a TLS-validation bypass.")
    probe = exact(value["probe"], ("host", "path", "port", "statusCodes"), "HTTPS health probe")
    if (probe["host"] != value["backendFqdn"] or probe["port"] != 443 or probe["statusCodes"] != ["200"]
            or not isinstance(probe["path"], str) or not re.fullmatch(r"/[A-Za-z0-9/_-]+", probe["path"])):
        raise GateError("The backend probe requires its observed host, explicit health path, HTTPS443 and exact success200.")
    limits = exact(value["limits"], ("bodyKb", "uploadMb", "requestTimeoutSeconds", "drainSeconds", "streaming"), "Reviewed WAF limits")
    for key, lower, upper in (("bodyKb", 8, 128), ("uploadMb", 1, 100), ("requestTimeoutSeconds", 1, 240), ("drainSeconds", 1, 3600)):
        if type(limits[key]) is not int or not lower <= limits[key] <= upper:
            raise GateError("WAF limits require explicit, bounded application evidence; no silent buffer/timeout expansion.")
    if limits["streaming"] != "none" or limits["drainSeconds"] < limits["requestTimeoutSeconds"]:
        raise GateError("Streaming is not qualified by the current web contract; draining must cover the reviewed request window.")


def validate_bindings(value, manifest, previous=None):
    exact(value, ("contract", "generation", "revision", "previousSha256", "changedConsumer", "consumers"), "Hub binding revision")
    if value["contract"] != BINDINGS_CONTRACT or value["generation"] != manifest["generation"]:
        raise GateError("Binding revision belongs to another contract/generation.")
    if (type(value["revision"]) is not int or value["revision"] != (previous["revision"] + 1 if previous else 1)
            or value["previousSha256"] != (digest(previous) if previous else None)):
        raise GateError("Hub binding revisions are monotonic and must bind the exact preceding snapshot.")
    if value["changedConsumer"] not in CONSUMERS:
        raise GateError("One exact consumer must own this binding change.")
    exact(value["consumers"], CONSUMERS, "Consumer slots")
    allocations = [manifest["network"]["vnet"], manifest["network"]["vpnClientPool"]]
    hosts, records, certificate_scopes = set(), set(), set()
    for consumer, entry in value["consumers"].items():
        prior = previous["consumers"][consumer] if previous else None
        if consumer != value["changedConsumer"] and entry != prior:
            raise GateError("A consumer update may not add, change, omit or offboard the other consumer.")
        if entry is None:
            if prior is not None:
                raise GateError("Do not drop a consumer; explicit reference-targeted offboarding is required.")
            continue
        exact(entry, ("bindingHash", "deploymentId", "sourceCommit", "expiresAtUtc", "state", "spoke",
                      "edgePolicy", "privateEndpoints", "dnsRecords", "operatorEndpoints", "web", "offboarding"), "Consumer binding")
        sha(entry["bindingHash"], "consumer binding")
        require_guid(entry["deploymentId"], "consumer deploymentId")
        if entry["bindingHash"] == manifest["bindingHash"] or entry["deploymentId"] == manifest["deploymentId"]:
            raise GateError("Consumers cannot share the hub's ownership identity.")
        if not isinstance(entry["sourceCommit"], str) or not re.fullmatch(r"[0-9a-f]{40}", entry["sourceCommit"]):
            raise GateError("A consumer needs its exact source commit, not a movable branch.")
        if not parse_time(manifest["createdAtUtc"]) < parse_time(entry["expiresAtUtc"]) <= parse_time(manifest["expiresAtUtc"]):
            raise GateError("A consumer may not extend the original hub deadline.")
        if entry["state"] not in STATES:
            raise GateError("Unsupported consumer state.")
        if prior:
            for key in ("bindingHash", "deploymentId", "spoke", "expiresAtUtc", "sourceCommit", "edgePolicy"):
                if entry[key] != prior[key]:
                    raise GateError("An immutable consumer identity, source, address or deadline changed; use a new reviewed generation.")
            if (STATES.index(entry["state"]) < STATES.index(prior["state"])
                    or prior["state"] == "offboarded" and (entry != prior or consumer == value["changedConsumer"])):
                raise GateError("Consumer state cannot roll back or resurrect an offboarded generation.")
            if entry["state"] != "offboarded" and STATES.index(entry["state"]) > STATES.index(prior["state"]) + 1:
                raise GateError("Each peering, transit and data prerequisite needs its own observed revision.")
            if entry["state"] != "offboarded":
                for key in ("privateEndpoints", "dnsRecords", "operatorEndpoints"):
                    if any(item not in entry[key] for item in prior[key]):
                        raise GateError("Existing data references are immutable until targeted consumer offboarding.")
                if prior["web"] is not None and prior["web"] != entry["web"]:
                    raise GateError("An active web contract cannot be replaced within an immutable application generation.")
        elif entry["state"] != "reserved":
            raise GateError("A new consumer starts as a reservation, never an adopted live network.")
        validate_spoke(entry["spoke"], consumer, manifest)
        allocations.append(entry["spoke"]["addressPrefix"])
        sources = {key: subnet["addressPrefix"] for key, subnet in entry["spoke"]["subnets"].items()}
        policy(entry["edgePolicy"], sources)
        for key in ("privateEndpoints", "dnsRecords", "operatorEndpoints"):
            if not isinstance(entry[key], list):
                raise GateError("Endpoint and DNS reference sets must be explicit lists.")
        if entry["state"] in ("reserved", "peered", "connected") and any(entry[key] for key in ("privateEndpoints", "dnsRecords", "operatorEndpoints", "web")):
            raise GateError("Data/web references require a later bound state, not a reservation.")
        if entry["state"] == "web-bound":
            validate_web(entry["web"], consumer, entry["spoke"], manifest)
            certificate_scope = entry["web"]["certificateSecretId"].rsplit("/", 1)[0].lower()
            if certificate_scope in certificate_scopes:
                raise GateError("Each consumer needs a distinct certificate-secret scope for independently removable grants.")
            certificate_scopes.add(certificate_scope)
            if entry["web"]["frontendHost"] in hosts:
                raise GateError("The two consumers must have separate exact hosts/listeners/policies.")
            hosts.add(entry["web"]["frontendHost"])
            identity = (entry["web"]["frontendZone"], entry["web"]["frontendRecord"])
            if identity in records:
                raise GateError("A frontend cannot overwrite another consumer's data DNS record.")
            records.add(identity)
        elif entry["web"] is not None:
            raise GateError("A web binding may only be active in web-bound state.")
        if consumer == "primary" and entry["operatorEndpoints"]:
            raise GateError("Primary does not grant VPN access to data private endpoints.")
        endpoint_ids = set()
        for endpoint in entry["privateEndpoints"]:
            exact(endpoint, ("id", "serviceId", "subnetId", "groupIds", "fqdns"), "Private endpoint reference")
            arm_id(endpoint["id"], scope=entry["spoke"]["resourceGroupId"], kind="Microsoft.Network/privateEndpoints")
            arm_id(endpoint["serviceId"])
            allowed = {subnet["id"] for role, subnet in entry["spoke"]["subnets"].items()
                       if role in ("privateEndpoints", "endpoints", "operatorEndpoints")}
            if endpoint["subnetId"] not in allowed or not endpoint["groupIds"] or endpoint["id"] in endpoint_ids:
                raise GateError("A private endpoint needs its exact distinct consumer-owned subnet/service/group binding.")
            endpoint_ids.add(endpoint["id"])
            if not isinstance(endpoint["fqdns"], list) or not endpoint["fqdns"]:
                raise GateError("PE public/private service names need actual provider observations, not guessed aliases.")
            for fqdn in endpoint["fqdns"]:
                hostname(fqdn)
        for record in entry["dnsRecords"]:
            exact(record, ("zone", "name", "addresses", "sourceResourceId", "dnsEvidenceSha256"), "DNS record")
            hostname(record["zone"])
            if record["zone"] not in manifest["dnsZones"] and not record["zone"].endswith(".azurecontainerapps.io"):
                raise GateError("Dynamic zones are restricted to the actual provider-owned Container Apps suffix.")
            if not isinstance(record["name"], str) or not re.fullmatch(r"(?:[a-z0-9_][a-z0-9_.-]*|@|\*)", record["name"]):
                raise GateError("A DNS record needs an exact source-bound relative name.")
            identity = (record["zone"], record["name"])
            if identity in records:
                raise GateError("Consumers cannot share or overwrite a DNS record set.")
            records.add(identity)
            arm_id(record["sourceResourceId"], scope=entry["spoke"]["resourceGroupId"])
            sha(record["dnsEvidenceSha256"])
            if not isinstance(record["addresses"], list) or not record["addresses"]:
                raise GateError("DNS record addresses require actual endpoint/static-IP observations.")
            for address in record["addresses"]:
                private_ip(address, entry["spoke"]["addressPrefix"])
        for endpoint in entry["operatorEndpoints"]:
            exact(endpoint, ("privateEndpointId", "addresses", "fqdns", "port"), "Operator PE exception")
            if consumer != "fabric" or endpoint["privateEndpointId"] not in endpoint_ids or endpoint["port"] != 443:
                raise GateError("Only Fabric's explicitly bound operator endpoints may admit VPN HTTPS.")
            bound = next(item for item in entry["privateEndpoints"] if item["id"] == endpoint["privateEndpointId"])
            if bound["subnetId"] != entry["spoke"]["subnets"]["operatorEndpoints"]["id"] or bound["groupIds"] not in (["vault"], ["blob"], ["workspace"]):
                raise GateError("VPN does not grant direct VM, registry, hosted runtime or database access.")
            if not endpoint["addresses"] or not endpoint["fqdns"]:
                raise GateError("Operator endpoint rules require actual IP/FQDN evidence.")
            for address in endpoint["addresses"]:
                private_ip(address, entry["spoke"]["subnets"]["operatorEndpoints"]["addressPrefix"])
            for fqdn in endpoint["fqdns"]:
                hostname(fqdn)
        if entry["state"] == "offboarded":
            exact(entry["offboarding"], ("stoppedWorkEvidenceSha256", "credentialsAndGrantsRevokedEvidenceSha256", "spokePeeringAbsentEvidenceSha256"), "Consumer offboarding")
            for evidence in entry["offboarding"].values():
                sha(evidence)
            if any(entry[key] for key in ("privateEndpoints", "dnsRecords", "operatorEndpoints", "web")):
                raise GateError("Offboarding must remove this consumer's references, not retain network access.")
        elif entry["offboarding"] is not None:
            raise GateError("Offboarding evidence is only valid for an explicitly retired consumer.")
    nonoverlap(allocations)
    for allocation in allocations:
        if any(cidr(allocation).overlaps(ipaddress.ip_network(reserved)) for reserved in ("172.30.0.0/16", "172.31.0.0/16")):
            raise GateError("The routed topology must avoid the documented ACA/AKS reserved ranges.")
    return value


def active_consumers(bindings):
    return [{"key": key, **copy.deepcopy(entry)} for key, entry in bindings["consumers"].items()
            if entry and entry["state"] not in ("reserved", "offboarded")]


def hub_policy(manifest, bindings, hub=None):
    selected = policy(manifest["edgePolicy"], {"hubWaf": manifest["network"]["waf"]})
    domains = set(selected["dnsDomains"]) | set(manifest["dnsZones"])
    rules = [{**item, "name": "hub-" + item["name"]} for item in selected["httpsRules"]]
    sources = [manifest["network"]["waf"], manifest["network"]["vpnClientPool"]]
    private = set()
    if hub:
        vault_host = urllib.parse.urlsplit(hub["ownerDependencies"]["certificateVaultUri"]).hostname
        hostname(vault_host)
        private.add(vault_host)
        domains.add(vault_host)
    for entry in active_consumers(bindings):
        prefixes = {key: subnet["addressPrefix"] for key, subnet in entry["spoke"]["subnets"].items()}
        item = policy(entry["edgePolicy"], prefixes)
        domains.update(item["dnsDomains"])
        rules.extend({**rule, "name": entry["key"] + "-" + rule["name"]} for rule in item["httpsRules"])
        sources.extend(prefixes.values())
        private.update(fqdn for endpoint in entry["privateEndpoints"] for fqdn in endpoint["fqdns"])
        domains.update(private)
        for record in entry["dnsRecords"]:
            domains.add(record["zone"])
            if record["name"] not in ("@", "*"):
                private.add(record["name"] + "." + record["zone"])
                domains.add(record["name"] + "." + record["zone"])
        if entry["web"]:
            domains.update((entry["web"]["backendFqdn"], entry["web"]["frontendHost"], entry["web"]["frontendZone"]))
            private.add(entry["web"]["backendFqdn"])
    return {"dnsDomains": sorted(domains), "httpsRules": rules, "sourcePrefixes": sorted(set(sources)),
            "privateOnlyFqdns": sorted(private)}


def validate_wire_receipt(receipt, *, now=None):
    exact(receipt, ("contract", "generation", "revision", "hubBindingHash", "consumer", "consumerBindingHash",
                    "state", "expiresAtUtc", "hub", "spoke", "peering", "vpn", "dns", "egress", "web",
                    "operatorEndpoints", "ownership", "readback", "receiptSha256"), "Hub consumer receipt")
    if receipt["contract"] != RECEIPT_CONTRACT or receipt["consumer"] not in CONSUMERS or receipt["state"] not in STATES:
        raise GateError("Only the versioned shared-hub consumer contract is accepted.")
    require_guid(receipt["generation"], "hub generation")
    for key in ("hubBindingHash", "consumerBindingHash", "receiptSha256"):
        sha(receipt[key], key)
    if receipt["hubBindingHash"] == receipt["consumerBindingHash"]:
        raise GateError("Hub and consumer cannot share ownership.")
    unsigned = {key: value for key, value in receipt.items() if key != "receiptSha256"}
    if digest(unsigned) != receipt["receiptSha256"] or type(receipt["revision"]) is not int or receipt["revision"] < 1:
        raise GateError("Hub receipt hash/revision mismatch.")
    hub = exact(receipt["hub"], ("resourceGroupId", "vnetId", "location", "addressPrefix", "subnets", "firewallId",
                                 "firewallPolicyId", "firewallPrivateIp", "vpnGatewayId", "dnsPolicyId", "dnsServerIp",
                                 "wafId", "wafPrivateIp"), "Receipt hub")
    group = arm_id(hub["resourceGroupId"])
    if "/providers/" in group:
        raise GateError("The hub ownership boundary must be a resource group.")
    for key, kind in (("vnetId", "virtualNetworks"), ("firewallId", "azureFirewalls"),
                      ("firewallPolicyId", "firewallPolicies"), ("vpnGatewayId", "virtualNetworkGateways"),
                      ("dnsPolicyId", "dnsResolverPolicies")):
        arm_id(hub[key], scope=group, kind="Microsoft.Network/" + kind)
    exact(hub["subnets"], HUB_SUBNETS, "Hub subnet roles")
    for role, subnet in hub["subnets"].items():
        exact(subnet, ("id", "addressPrefix"), "Hub subnet")
        arm_id(subnet["id"], scope=hub["vnetId"], kind="Microsoft.Network/virtualNetworks/subnets")
        if not cidr(subnet["addressPrefix"]).subnet_of(cidr(hub["addressPrefix"])):
            raise GateError("Hub subnet lies outside its owned VNet.")
    nonoverlap([item["addressPrefix"] for item in hub["subnets"].values()])
    private_ip(hub["firewallPrivateIp"], hub["subnets"]["firewall"]["addressPrefix"])
    if hub["dnsServerIp"] != hub["firewallPrivateIp"]:
        raise GateError("DNS must use the observed hub Firewall proxy.")
    if (hub["wafId"] is None) != (hub["wafPrivateIp"] is None):
        raise GateError("WAF identity and private address must be absent or observed together.")
    if hub["wafId"] is not None:
        arm_id(hub["wafId"], scope=group, kind="Microsoft.Network/applicationGateways")
        private_ip(hub["wafPrivateIp"], hub["subnets"]["waf"]["addressPrefix"])
    context = {"binding": {"subscriptionId": group.split("/")[2]},
               "names": {"hubResourceGroup": group.split("/")[4]}, "location": hub["location"]}
    validate_spoke(receipt["spoke"], receipt["consumer"], context)
    vpn = exact(receipt["vpn"], ("audienceClientId", "servicePrincipalObjectId", "assignmentRequired", "userSetSha256",
                                 "resolvedUsersEvidenceSha256", "businessRolesChanged", "clientPool"), "VPN")
    for key in ("audienceClientId", "servicePrincipalObjectId"):
        require_guid(vpn[key], key)
    for key in ("userSetSha256", "resolvedUsersEvidenceSha256"):
        sha(vpn[key], key)
    if vpn["assignmentRequired"] is not True or vpn["businessRolesChanged"] is not False:
        raise GateError("Shared VPN assignment may not grant application or directory roles.")
    nonoverlap([hub["addressPrefix"], receipt["spoke"]["addressPrefix"], vpn["clientPool"]])
    dns = exact(receipt["dns"], ("owner", "registrationMode", "zones", "recordSets"), "DNS")
    if dns["owner"] != "hub" or dns["registrationMode"] != "hub-owner-only-no-consumer-zone-groups":
        raise GateError("Consumers cannot mutate shared DNS or indirectly write through PE zone groups.")
    if not isinstance(dns["zones"], list) or not dns["zones"] or not isinstance(dns["recordSets"], list):
        raise GateError("Exact hub DNS namespaces and record references are required.")
    zone_ids = set()
    for zone in dns["zones"]:
        exact(zone, ("name", "id"), "DNS zone")
        hostname(zone["name"])
        if zone["id"] != group + "/providers/Microsoft.Network/privateDnsZones/" + zone["name"] or zone["id"] in zone_ids:
            raise GateError("Duplicate or foreign DNS zone binding.")
        zone_ids.add(zone["id"])
    for identifier in dns["recordSets"]:
        arm_id(identifier, scope=group, kind="Microsoft.Network/privateDnsZones/A")
        if identifier.rsplit("/A/", 1)[0] not in zone_ids:
            raise GateError("DNS record is outside the bound namespaces.")
    if len(set(dns["recordSets"])) != len(dns["recordSets"]):
        raise GateError("Duplicate DNS record references.")
    egress = exact(receipt["egress"], ("defaultNextHop", "nextHopIp", "bgpPropagation", "hubPrefixRoute",
                                      "spokeToSpoke", "vpnDatabaseAccess", "dnsDomains", "httpsRules"), "Egress")
    if egress["nextHopIp"] != hub["firewallPrivateIp"]:
        raise GateError("Egress must use the observed hub Firewall.")
    for key, value in (("defaultNextHop", "VirtualAppliance"), ("bgpPropagation", True),
                       ("hubPrefixRoute", False), ("spokeToSpoke", False), ("vpnDatabaseAccess", False)):
        if type(egress[key]) is not type(value) or egress[key] != value:
            raise GateError("Hub routing/access boundary changed.")
    if not isinstance(egress["dnsDomains"], list) or not isinstance(egress["httpsRules"], list):
        raise GateError("DNS/HTTPS policy lists are required.")
    for domain in egress["dnsDomains"]:
        hostname(domain)
    source_prefixes = {item["addressPrefix"] for item in receipt["spoke"]["subnets"].values()}
    names = set()
    for rule in egress["httpsRules"]:
        exact(rule, ("name", "sourceAddresses", "targetFqdns"), "Emitted HTTPS rule")
        if (not isinstance(rule["name"], str) or not rule["name"].startswith(receipt["consumer"] + "-")
                or rule["name"] in names or not rule["sourceAddresses"] or not rule["targetFqdns"]
                or not set(rule["sourceAddresses"]) <= source_prefixes):
            raise GateError("Receipt HTTPS rules must retain their consumer-prefixed names and own sources.")
        names.add(rule["name"])
        for fqdn in rule["targetFqdns"]:
            hostname(fqdn, wildcard=True)
    if receipt["state"] in ("reserved", "offboarded"):
        if receipt["peering"] is not None:
            raise GateError("A reserved/retired consumer has no live paired peering.")
    else:
        exact(receipt["peering"], ("hubToSpoke", "spokeToHub"), "Peering pair")
        for direction, local, remote in (("hubToSpoke", hub["vnetId"], receipt["spoke"]["vnetId"]),
                                         ("spokeToHub", receipt["spoke"]["vnetId"], hub["vnetId"])):
            peer = exact(receipt["peering"][direction], ("id", "remoteVirtualNetworkId", "allowVirtualNetworkAccess",
                         "allowForwardedTraffic", "allowGatewayTransit", "useRemoteGateways", "peeringState"), "Peering")
            suffix = "/virtualNetworkPeerings/" + ("to-" + receipt["consumer"] if direction == "hubToSpoke" else "to-shared-hub")
            expected = {"id": local + suffix, "remoteVirtualNetworkId": remote, "allowVirtualNetworkAccess": True,
                        "allowForwardedTraffic": direction == "spokeToHub", "allowGatewayTransit": direction == "hubToSpoke",
                        "useRemoteGateways": direction == "spokeToHub" and receipt["state"] != "peered", "peeringState": "Connected"}
            if any(type(peer[key]) is not type(value) or peer[key] != value for key, value in expected.items()):
                raise GateError("The paired peering/transit flags differ from this handshake state.")
    if receipt["state"] == "web-bound":
        validate_web(receipt["web"], receipt["consumer"], receipt["spoke"], context)
        if not hub["wafId"]:
            raise GateError("A web-bound receipt requires the actual private WAF.")
    elif receipt["web"] is not None:
        raise GateError("No web binding before the explicit web-bound state or after offboarding.")
    if not isinstance(receipt["operatorEndpoints"], list) or receipt["consumer"] == "primary" and receipt["operatorEndpoints"]:
        raise GateError("Primary has no direct VPN-to-data exception.")
    for endpoint in receipt["operatorEndpoints"]:
        exact(endpoint, ("privateEndpointId", "addresses", "fqdns", "port"), "Operator endpoint")
        arm_id(endpoint["privateEndpointId"], scope=receipt["spoke"]["resourceGroupId"], kind="Microsoft.Network/privateEndpoints")
        if endpoint["port"] != 443 or not endpoint["addresses"] or not endpoint["fqdns"]:
            raise GateError("Only explicit Fabric operator HTTPS endpoints are allowed.")
        for address in endpoint["addresses"]:
            private_ip(address, receipt["spoke"]["subnets"]["operatorEndpoints"]["addressPrefix"])
        for fqdn in endpoint["fqdns"]:
            hostname(fqdn)
    if receipt["state"] in ("reserved", "peered", "connected", "offboarded") and (dns["recordSets"] or receipt["operatorEndpoints"]):
        raise GateError("Data references require a later observed binding state.")
    if receipt["ownership"] != OWNERSHIP:
        raise GateError("The receipt changes shared ownership or failure-domain boundaries.")
    readback = exact(receipt["readback"], ("observedAtUtc", "infrastructureCommit", "compiledTemplateSha256",
                      "deploymentResourceId", "resources", "trafficAccepted", "capacityAccepted", "costAccepted"), "Readback")
    if not isinstance(readback["infrastructureCommit"], str) or not re.fullmatch(r"[0-9a-f]{40}", readback["infrastructureCommit"]):
        raise GateError("Readback must bind an immutable source commit.")
    sha(readback["compiledTemplateSha256"])
    if not re.fullmatch(re.escape("/subscriptions/" + group.split("/")[2]) + r"/providers/Microsoft.Resources/deployments/[A-Za-z0-9_-]+", readback["deploymentResourceId"] or ""):
        raise GateError("Readback must bind its exact owner subscription deployment.")
    if any(readback[key] is not False for key in ("trafficAccepted", "capacityAccepted", "costAccepted")):
        raise GateError("ARM metadata cannot grant traffic/capacity/cost acceptance.")
    if not isinstance(readback["resources"], list) or not readback["resources"]:
        raise GateError("Actual ARM observation bindings are missing.")
    now = now or utcnow()
    observed, expires = parse_time(readback["observedAtUtc"]), parse_time(receipt["expiresAtUtc"])
    until = observed + dt.timedelta(hours=24)
    if receipt["state"] != "offboarded":
        until = min(expires, until)
    if not observed <= now < until:
        raise GateError("Hub readback is future-dated, expired or older than twenty-four hours; refresh it.")
    return receipt


def validate_receipt(receipt, manifest, *, now=None, allow_offboarded=False):
    validate_wire_receipt(receipt, now=now)
    if receipt["consumer"] != "primary":
        raise GateError("The Primary installer requires its own shared-hub receipt.")
    expected = manifest["hubBinding"]
    if (receipt["generation"] != expected["generation"] or receipt["hubBindingHash"] != expected["bindingHash"]
            or receipt["consumerBindingHash"] != manifest["bindingHash"]
            or receipt["hub"]["resourceGroupId"] != expected["resourceGroupId"]
            or receipt["readback"]["infrastructureCommit"] != expected["infrastructureCommit"]
            or receipt["expiresAtUtc"] != manifest["expiresAtUtc"]):
        raise GateError("The receipt is not bound to the exact hub generation, source, consumer and original deadline.")
    if receipt["state"] == "offboarded" and not allow_offboarded:
        raise GateError("A retired hub consumer cannot activate resources.")
    group = f"/subscriptions/{manifest['binding']['subscriptionId']}/resourceGroups/{manifest['names']['workloadResourceGroup']}"
    if receipt["spoke"]["resourceGroupId"] != group or receipt["spoke"]["addressPrefix"] != manifest["network"]["vnet"]:
        raise GateError("The reservation belongs to a different Primary spoke.")
    vnet = group + "/providers/Microsoft.Network/virtualNetworks/vnet-" + manifest["namePrefix"] + "-spoke"
    if receipt["spoke"]["vnetId"] != vnet or receipt["spoke"]["location"] != manifest["location"]:
        raise GateError("The reservation must use the actual version3 Primary resource naming and region.")
    for key in PRIMARY_SUBNETS:
        if receipt["spoke"]["subnets"][key] != {"id": vnet + "/subnets/" + PRIMARY_SUBNET_NAMES[key],
                                               "addressPrefix": manifest["network"][key]}:
            raise GateError("A Primary subnet allocation changed.")
    expected_policy = policy(manifest["edgePolicy"], {key: manifest["network"][key] for key in PRIMARY_SUBNETS})
    expected_rules = [{**rule, "name": "primary-" + rule["name"]} for rule in expected_policy["httpsRules"]]
    if receipt["egress"]["httpsRules"] != (expected_rules if receipt["state"] != "offboarded" else []):
        raise GateError("Hub may not change Primary's reviewed outbound HTTPS contract.")
    return receipt


def latest_receipt(outputs, *, required_state=None):
    items = [value for key, value in outputs.items() if key.startswith("hub-receipt-")]
    if not items:
        raise GateError("Import a fresh source-bound shared-hub receipt before the Primary network phase.")
    receipt = max(items, key=lambda value: (value["revision"], parse_time(value["readback"]["observedAtUtc"])))
    if required_state and STATES.index(receipt["state"]) < STATES.index(required_state):
        raise GateError(f"The shared hub must be {required_state}; unverified backend/DNS values cannot be substituted.")
    if receipt["state"] == "offboarded" and required_state != "offboarded":
        raise GateError("The consumer is offboarded.")
    return receipt


def verify_observations(client, receipt):
    scopes = [receipt["hub"]["resourceGroupId"], receipt["spoke"]["resourceGroupId"]]
    seen, values = set(), {}
    for item in receipt["readback"]["resources"]:
        exact(item, ("id", "apiVersion", "sha256", "etag"), "ARM observation")
        identifier = arm_id(item["id"])
        if not any(identifier.lower().startswith(scope.lower() + "/") for scope in scopes) or identifier.lower() in seen:
            raise GateError("Observation references an unbound/duplicate resource.")
        if item["apiVersion"] not in (NETWORK_API, WAF_API, DNS_API, POLICY_API, "2025-07-01", "2024-11-01", "2024-03-01", "2024-11-30", "2022-04-01"):
            raise GateError("Unsupported readback API version.")
        sha(item["sha256"])
        seen.add(identifier.lower())
    for item in receipt["readback"]["resources"]:
        identifier = item["id"]
        actual = client.get(ARM + identifier + "?api-version=" + item["apiVersion"])
        if (actual.get("id", "").lower() != identifier.lower() or digest(actual) != item["sha256"]
                or actual.get("etag") != item["etag"]):
            raise GateError("A hub/spoke resource changed since the owner-issued receipt; no activation.")
        values[identifier] = actual
    for identifier in (receipt["hub"]["vnetId"], receipt["hub"]["firewallId"], receipt["hub"]["firewallPolicyId"],
                       receipt["hub"]["vpnGatewayId"], receipt["hub"]["dnsPolicyId"]):
        if identifier.lower() not in seen:
            raise GateError("The receipt omits a mandatory actual hub resource readback.")
    if receipt["state"] not in ("reserved", "offboarded"):
        if receipt["spoke"]["vnetId"].lower() not in seen or not receipt["peering"]:
            raise GateError("Both actual spoke and peering directions must be read back.")
        for direction in ("hubToSpoke", "spokeToHub"):
            peer = receipt["peering"][direction]
            expected = {"allowVirtualNetworkAccess": True, "allowForwardedTraffic": direction == "spokeToHub",
                        "allowGatewayTransit": direction == "hubToSpoke", "useRemoteGateways": direction == "spokeToHub" and receipt["state"] != "peered",
                        "peeringState": "Connected"}
            actual = values.get(peer["id"], {}).get("properties", {})
            if (peer["id"].lower() not in seen or any(peer.get(key) != value or actual.get(key) != value for key, value in expected.items())
                    or actual.get("remoteVirtualNetwork", {}).get("id") != peer["remoteVirtualNetworkId"]):
                raise GateError("Peering/gateway-transit flags or connected readback differ.")
    if receipt["state"] == "web-bound" and (not receipt["web"] or receipt["hub"]["wafId"].lower() not in seen):
        raise GateError("No web activation without the exact private WAF readback.")
    from shared_hub_control import verify_receipt_resources
    verify_receipt_resources(receipt, values)
