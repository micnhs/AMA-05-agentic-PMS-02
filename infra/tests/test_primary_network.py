import copy
import json
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest
from unittest.mock import MagicMock, patch


sys.path.insert(0, str(Path(__file__).parents[1] / "scripts"))
import primary_bootstrap as bootstrap
import primary_host as host
import primary_network as network
import primary_release as release
from primary_control import ARM, GRAPH, GateError


def identifier(number):
    return f"00000000-0000-4000-8000-{number:012d}"


class ReadOnlyClient:
    def __init__(self, resources=None, collections=None):
        self.resources = resources or {}
        self.collections = collections or {}

    def get(self, url):
        return copy.deepcopy(self.resources[url])

    def collection(self, url):
        return copy.deepcopy(self.collections[url])


class PrimaryNetworkTests(unittest.TestCase):
    def setUp(self):
        self.manifest = {
            "contract": "cohors-primary-bootstrap-2", "azureFirewall": True, "vpn": True,
            "containerAppsProfile": "D4", "location": "francecentral", "bindingHash": "b" * 64,
            "deploymentId": identifier(8), "namePrefix": "cohorstest", "expiresAtUtc": "2026-11-25T12:00:00Z",
            "binding": {"tenantId": identifier(9), "subscriptionId": identifier(10)},
            "names": {"workloadResourceGroup": "rg-cohorstest-workload",
                      "accessResourceGroup": "rg-cohorstest-access"},
            "network": {
                "vnet": "10.42.0.0/16", "containerApps": "10.42.0.0/23",
                "workflows": "10.42.2.0/26", "operations": "10.42.2.64/26",
                "privateEndpoints": "10.42.3.0/24", "operator": "10.42.4.32/27",
                "client": "10.42.4.64/27", "bastion": "10.42.5.0/26",
                "firewall": "10.42.6.0/26", "gateway": "10.42.6.64/27",
                "vpnClientPool": "172.28.240.0/24", "zones": ["1", "2", "3"],
            },
            "actors": [{"objectId": identifier(i)} for i in (1, 2, 3)],
            "vpnUserObjectIds": [identifier(i) for i in (1, 2, 3, 4)],
            "access": {"sshPublicKey": "synthetic-public-key", "includeInteractiveClient": False,
                       "linuxImage": {}, "linuxVmSize": "reviewed-candidate", "windowsImage": {},
                       "windowsVmSize": "reviewed-candidate", "clientOperatorObjectIds": []},
            "model": {"region": "francecentral", "enabled": False, "name": "reviewed-candidate",
                      "version": "reviewed-candidate", "deploymentName": "reviewed-candidate", "capacity": 1},
            "alertEmailReceivers": [], "productGrants": [], "products": [],
            "edgePolicy": {
                "dnsDomains": ["mcr.microsoft.com"],
                "httpsRules": [{"name": "platform-images", "sourceSegments": ["containerApps"],
                                "targetFqdns": ["mcr.microsoft.com"]}],
            },
        }
        self.group = bootstrap.own_group_ids(self.manifest)[0]
        self.prefix = self.group + "/providers/Microsoft.Network/"
        self.vnet = self.prefix + "virtualNetworks/vnet-test"
        ids = {"firewallId": self.prefix + "azureFirewalls/afw-test",
               "firewallPolicyId": self.prefix + "firewallPolicies/afwp-test",
               "firewallPublicIpId": self.prefix + "publicIPAddresses/pip-firewall",
               "gatewayId": self.prefix + "virtualNetworkGateways/vgw-test",
               "gatewayPublicIpId": self.prefix + "publicIPAddresses/pip-vpn",
               "dnsPolicyId": self.prefix + "dnsResolverPolicies/dnsp-test",
               "deniedDomainsId": self.prefix + "dnsResolverDomainLists/dnsdl-denied",
               "allowedDomainsId": self.prefix + "dnsResolverDomainLists/dnsdl-allowed"}
        ids.update({
            "allowDnsRuleId": ids["dnsPolicyId"] + "/dnsSecurityRules/allow-reviewed-domains",
            "denyDnsRuleId": ids["dnsPolicyId"] + "/dnsSecurityRules/deny-unmatched",
            "dnsLinkId": ids["dnsPolicyId"] + "/virtualNetworkLinks/own-primary-only",
            "privateDataRulesId": ids["firewallPolicyId"] + "/ruleCollectionGroups/private-data-no-egress",
            "httpsRulesId": ids["firewallPolicyId"] + "/ruleCollectionGroups/reviewed-https",
        })
        self.stage = {"network": {"id": self.vnet,
                                 "routeTableId": self.prefix + "routeTables/rt-workload",
                                 "gatewayRouteTableId": self.prefix + "routeTables/rt-gateway",
                                 "dns": {"blob": self.prefix + "privateDnsZones/privatelink.blob.core.windows.net",
                                         "foundry": [self.prefix + "privateDnsZones/privatelink.openai.azure.com"]}},
                      "edge": {"resources": ids, "firewallPrivateIp": "10.42.6.10"},
                      "access": {"operatorVmId": "not-executed", "identities": {}}}
        self.identity = {"objectId": identifier(11), "clientId": identifier(12),
                         "servicePrincipalObjectId": identifier(13)}
        self.platform = {
            "endpoints": {"sqlServer": "sql-test.database.windows.net",
                          "evidenceContainer": "https://evidence.blob.core.windows.net/evidence"},
            "privateServiceEndpoints": {
                "registryData": ["test.francecentral.data.azurecr.io"],
                "telemetry": ["https://francecentral-0.in.applicationinsights.azure.com/"],
            },
            "containerEnvironmentDomain": "test.francecentral.azurecontainerapps.io",
            "runtimeUrls": {"api": "https://api.test.francecentral.azurecontainerapps.io",
                            "operations": "https://ops.azurewebsites.net"},
            "runtimePrivateHostNames": {
                "operations": ["ops.azurewebsites.net", "ops.scm.azurewebsites.net"],
                "workflows": ["workflows.azurewebsites.net", "workflows.scm.azurewebsites.net"],
            },
        }

    def resource(self, resource_id, kind, properties, *, tagged=True):
        return {"id": resource_id, "type": kind, "location": "francecentral",
                "tags": {"cohorsBinding": "b" * 64} if tagged else None,
                "properties": {"provisioningState": "Succeeded", **properties}}

    def test_telemetry_projection_uses_actual_owned_metadata_without_instrumentation_identifiers(self):
        identifier = self.group + "/providers/Microsoft.Insights/components/ai-observed"
        component = self.resource(identifier, "Microsoft.Insights/components", {
            "DisableLocalAuth": True, "ConnectionString": "InstrumentationKey=synthetic-do-not-persist;"
            "IngestionEndpoint=https://observed.in.applicationinsights.azure.com/;"
            "LiveEndpoint=https://observed.livediagnostics.monitor.azure.com/;ApplicationId=synthetic-do-not-persist"})
        platform = {"resources": {"applicationInsights": identifier}, "privateServiceEndpoints": {
            "registryData": ["actual.francecentral.data.azurecr.io"], "telemetryResourceId": identifier}}
        client = ReadOnlyClient({ARM + identifier + "?api-version=2020-02-02": component})
        result = network.observe_private_service_endpoints(client, self.manifest, platform)
        self.assertEqual(platform["privateServiceEndpoints"]["telemetry"], [
            "https://observed.in.applicationinsights.azure.com/",
            "https://observed.livediagnostics.monitor.azure.com/"])
        self.assertNotIn("synthetic-do-not-persist", json.dumps([platform, result]))
        self.assertFalse(result["trafficAccepted"])
        for connection in ("InstrumentationKey=synthetic-do-not-persist",
                           component["properties"]["ConnectionString"] + ";IngestionEndpoint=https://duplicate.example.test/"):
            client.resources[ARM + identifier + "?api-version=2020-02-02"]["properties"]["ConnectionString"] = connection
            platform["privateServiceEndpoints"] = {"registryData": ["actual.francecentral.data.azurecr.io"],
                                                   "telemetryResourceId": identifier}
            with self.assertRaises(GateError):
                network.observe_private_service_endpoints(client, self.manifest, platform)

    def test_resource_names_are_projected_from_verified_ids_without_mutating_previous_outputs(self):
        outputs = {"phase-edge-bind": self.stage, "phase-identities": {}}
        before = copy.deepcopy(self.stage)
        parameters = bootstrap.arm_parameters(self.manifest, "foundation", outputs)
        self.assertEqual(parameters["networkPhase"]["value"]["edge"]["resourceNames"],
                         {"firewall": "afw-test", "gateway": "vgw-test"})
        self.assertEqual(self.stage, before)
        foreign = "/subscriptions/" + identifier(10) + "/resourceGroups/not-owned/providers/Microsoft.Insights/components/ai"
        client = MagicMock()
        with self.assertRaisesRegex(GateError, "outside"):
            network.observe_private_service_endpoints(client, self.manifest, {"resources": {"applicationInsights": foreign},
                "privateServiceEndpoints": {"registryData": ["actual.francecentral.data.azurecr.io"], "telemetryResourceId": foreign}})
        client.get.assert_not_called()

    def edge_client(self, bound):
        ids, ranges = self.stage["edge"]["resources"], self.manifest["network"]
        private_ip = self.stage["edge"]["firewallPrivateIp"]
        subnets = []
        for key, name in {
            "containerApps": "snet-container-apps", "workflows": "snet-workflows",
            "operations": "snet-operations", "privateEndpoints": "snet-private-endpoints",
            "operator": "snet-operator", "client": "snet-test-client", "bastion": "AzureBastionSubnet",
            "firewall": "AzureFirewallSubnet", "gateway": "GatewaySubnet",
        }.items():
            properties = {"addressPrefix": ranges[key]}
            if key in (*network.SOURCE_SEGMENTS, "gateway"):
                properties["routeTable"] = {"id": self.stage["network"][
                    "gatewayRouteTableId" if key == "gateway" else "routeTableId"]}
            subnets.append({"name": name, "properties": properties})
        gateway = {"gatewayType": "Vpn", "vpnType": "RouteBased", "vpnGatewayGeneration": "Generation1",
                   "sku": {"name": "VpnGw1AZ", "tier": "VpnGw1AZ"}, "activeActive": False, "enableBgp": False,
                   "ipConfigurations": [{"properties": {"subnet": {"id": self.vnet + "/subnets/GatewaySubnet"},
                                                         "publicIPAddress": {"id": ids["gatewayPublicIpId"]}}}]}
        if bound:
            gateway["vpnClientConfiguration"] = {
                "aadTenant": "https://login.microsoftonline.com/" + self.manifest["binding"]["tenantId"],
                "aadIssuer": "https://sts.windows.net/" + self.manifest["binding"]["tenantId"] + "/",
                "aadAudience": self.identity["clientId"], "vpnAuthenticationTypes": ["AAD"],
                "vpnClientProtocols": ["OpenVPN"],
                "vpnClientAddressPool": {"addressPrefixes": [ranges["vpnClientPool"]]},
            }
        values = [
            self.resource(ids["firewallId"], "Microsoft.Network/azureFirewalls",
                          {"sku": {"name": "AZFW_VNet", "tier": "Standard"},
                           "firewallPolicy": {"id": ids["firewallPolicyId"]},
                           "ipConfigurations": [{"properties": {
                               "privateIPAddress": private_ip, "publicIPAddress": {"id": ids["firewallPublicIpId"]},
                               "subnet": {"id": self.vnet + "/subnets/AzureFirewallSubnet"}}}]}),
            self.resource(ids["firewallPolicyId"], "Microsoft.Network/firewallPolicies",
                          {"sku": {"tier": "Standard"}, "threatIntelMode": "Deny",
                           "dnsSettings": {"enableProxy": True, "requireProxyForNetworkRules": True}}, tagged=False),
            self.resource(ids["gatewayId"], "Microsoft.Network/virtualNetworkGateways", gateway),
            self.resource(self.vnet, "Microsoft.Network/virtualNetworks",
                          {"addressSpace": {"addressPrefixes": [ranges["vnet"]]},
                           "dhcpOptions": {"dnsServers": [private_ip] if bound else []}, "subnets": subnets}),
        ]
        values[0]["zones"] = ranges["zones"][:]
        for key in ("firewallPublicIpId", "gatewayPublicIpId"):
            public_ip = self.resource(ids[key], "Microsoft.Network/publicIPAddresses",
                                      {"publicIPAllocationMethod": "Static", "publicIPAddressVersion": "IPv4"})
            public_ip.update(sku={"name": "Standard", "tier": "Regional"}, zones=ranges["zones"][:])
            values.append(public_ip)
        for key, propagation, prefixes in (
            ("routeTableId", True, ["0.0.0.0/0"] + ([ranges["vpnClientPool"]] if bound else [])),
            ("gatewayRouteTableId", False, [ranges[name] for name in
                                           ("containerApps", "workflows", "operations", "privateEndpoints",
                                            "operator", "client", "bastion")]),
        ):
            routes = [{"properties": {"addressPrefix": prefix,
                                     "nextHopType": "VirtualAppliance" if bound else "None",
                                     **({"nextHopIpAddress": private_ip} if bound else {})}} for prefix in prefixes]
            values.append(self.resource(self.stage["network"][key], "Microsoft.Network/routeTables",
                                        {"disableBgpRoutePropagation": propagation, "routes": routes}))
        return ReadOnlyClient({ARM + value["id"] + "?api-version=" + network.NETWORK_VERSION: value
                               for value in values})

    def policy_client(self, selected, bound):
        ids = self.stage["edge"]["resources"]
        values = [self.resource(ids["dnsPolicyId"], "Microsoft.Network/dnsResolverPolicies", {}),
                  self.resource(ids["deniedDomainsId"], "Microsoft.Network/dnsResolverDomainLists",
                                {"domains": ["."]}),
                  self.resource(ids["allowedDomainsId"], "Microsoft.Network/dnsResolverDomainLists",
                                {"domains": selected["dnsDomains"]})]
        rules = [{"id": ids[key], "properties": {
            "provisioningState": "Succeeded", "dnsSecurityRuleState": "Enabled",
            "priority": priority, "action": {"actionType": action},
            "dnsResolverDomainLists": [{"id": ids[domain_key]}],
        }} for key, priority, action, domain_key in (
            ("allowDnsRuleId", 100, "Allow", "allowedDomainsId"),
            ("denyDnsRuleId", 65000, "Block", "deniedDomainsId"))]
        links = [{"id": ids["dnsLinkId"], "properties": {
            "provisioningState": "Succeeded", "virtualNetwork": {"id": self.vnet},
        }}] if bound else []
        private_rules = [{
            "name": "private-data-stays-private", "ruleType": "NetworkRule", "ipProtocols": ["TCP"],
            "sourceAddresses": selected["sourcePrefixes"], "destinationFqdns": selected["privateOnlyFqdns"],
            "destinationPorts": ["443", "1433"],
        }] if selected["privateOnlyFqdns"] else []
        https_rules = [{**rule, "ruleType": "ApplicationRule", "terminateTLS": False,
                        "protocols": [{"protocolType": "Https", "port": 443}]}
                       for rule in selected["httpsRules"]]
        groups = [{"id": ids[key], "properties": {
            "provisioningState": "Succeeded", "priority": priority,
            "ruleCollections": [{
                "name": name, "priority": priority, "action": {"type": action},
                "ruleCollectionType": "FirewallPolicyFilterRuleCollection", "rules": contents,
            }] if contents else [],
        }} for key, name, priority, action, contents in (
            ("privateDataRulesId", "deny-private-data-egress", 100, "Deny", private_rules),
            ("httpsRulesId", "allow-reviewed-https", 200, "Allow", https_rules))]
        return ReadOnlyClient(
            {ARM + value["id"] + "?api-version=2025-05-01": copy.deepcopy(value) for value in values},
            {ARM + ids["dnsPolicyId"] + "/dnsSecurityRules?api-version=2025-05-01": copy.deepcopy(rules),
             ARM + ids["dnsPolicyId"] + "/virtualNetworkLinks?api-version=2025-05-01": links,
             ARM + ids["firewallPolicyId"] + "/ruleCollectionGroups?api-version=2024-07-01": copy.deepcopy(groups)})

    def test_selected_shape_refuses_legacy_scope_overlap_and_public_ranges(self):
        network.validate_network_manifest(self.manifest)
        for mutation in (
            lambda value: value.update(vpn=False),
            lambda value: value.update(containerAppsProfile="Consumption"),
            lambda value: value.update(nvaPolicy={}),
            lambda value: value["vpnUserObjectIds"].pop(),
            lambda value: value["network"].update(vpnClientPool="10.42.9.0/24"),
            lambda value: value["network"].update(vpnClientPool="172.30.240.0/24"),
            lambda value: value["network"].update(vpnClientPool="172.31.240.0/24"),
            lambda value: value["network"].update(firewall="10.42.6.0/27"),
            lambda value: value["network"].update(gateway="10.42.6.0/27"),
            lambda value: value["network"].update(vpnClientPool="8.8.8.0/24"),
        ):
            changed = copy.deepcopy(self.manifest)
            mutation(changed)
            with self.subTest(changed=changed["network"]), self.assertRaises(GateError):
                network.validate_network_manifest(changed)

    def test_policy_binds_array_zones_and_private_services_before_client_access(self):
        selected = network.normalized_policy(self.manifest, self.stage, self.platform)
        self.assertIn("privatelink.openai.azure.com", selected["dnsDomains"])
        self.assertIn("sql-test.database.windows.net", selected["privateOnlyFqdns"])
        self.assertIn("ops.scm.azurewebsites.net", selected["privateOnlyFqdns"])
        self.assertIn("test.francecentral.data.azurecr.io", selected["privateOnlyFqdns"])
        self.assertIn("francecentral-0.in.applicationinsights.azure.com", selected["privateOnlyFqdns"])
        self.assertNotIn("vpn-private-api", [rule["name"] for rule in selected["httpsRules"]])
        selected = network.normalized_policy(self.manifest, self.stage, self.platform, allow_clients=True)
        self.assertEqual(selected["httpsRules"][-1], {
            "name": "vpn-private-api", "sourceAddresses": ["172.28.240.0/24"],
            "targetFqdns": ["api.test.francecentral.azurecontainerapps.io"]})
        changed = copy.deepcopy(self.platform)
        changed["runtimeUrls"]["api"] = "https://another.example.invalid"
        with self.assertRaises(GateError):
            network.normalized_policy(self.manifest, self.stage, changed, allow_clients=True)
        changed = copy.deepcopy(self.platform)
        changed["privateServiceEndpoints"]["registryData"] = []
        with self.assertRaisesRegex(GateError, "ACR data and telemetry"):
            network.normalized_policy(self.manifest, self.stage, changed)

    def test_policy_refuses_catchalls_urls_and_vpn_as_general_egress_source(self):
        for mutation in (
            lambda value: value["edgePolicy"].update(dnsDomains=["."]),
            lambda value: value["edgePolicy"]["httpsRules"][0].update(targetFqdns=["*"]),
            lambda value: value["edgePolicy"]["httpsRules"][0].update(targetFqdns=["https://mcr.microsoft.com"]),
            lambda value: value["edgePolicy"]["httpsRules"][0].update(sourceSegments=["vpnClientPool"]),
        ):
            changed = copy.deepcopy(self.manifest)
            mutation(changed)
            with self.assertRaises(GateError):
                network.normalized_policy(changed)

    def test_publishing_uses_role_specific_provider_hostname_including_regional_names(self):
        for site, scm in (("ops.azurewebsites.net", "ops.scm.azurewebsites.net"),
                          ("ops-abc.francecentral-01.azurewebsites.net",
                           "ops-abc.scm.francecentral-01.azurewebsites.net")):
            platform = copy.deepcopy(self.platform)
            platform["runtimeUrls"]["operations"] = "https://" + site
            platform["runtimePrivateHostNames"]["operations"] = [site, scm]
            self.assertEqual(network.publishing_host(platform, "operations"), scm)
            for wrong in ([], [site], [scm], [site, scm, "another.scm.azurewebsites.net"],
                          ["workflows.azurewebsites.net", "workflows.scm.azurewebsites.net"]):
                platform["runtimePrivateHostNames"]["operations"] = wrong
                with self.subTest(hosts=wrong), self.assertRaises(GateError):
                    network.publishing_host(platform, "operations")

    def test_missing_operator_tool_cannot_trigger_implicit_package_install(self):
        request = {
            "applicationCommit": "a" * 40, "infrastructureCommit": "b" * 40, "vmId": "own-vm",
            "bindingHash": "b" * 64, "phase": "initialize", "sourceNetwork": "10.42.0.0/16",
            "platform": {"endpoints": {
                "sqlServer": "sql.database.windows.net", "search": "https://search.search.windows.net",
                "evidenceContainer": "https://evidence.blob.core.windows.net/evidence",
                "foundryOpenAi": "https://foundry.openai.azure.com",
            }},
        }
        metadata = {"resourceId": "own-vm", "tagsList": [{"name": "cohorsBinding", "value": "b" * 64}]}
        with (patch.object(sys, "argv", ["primary_host", "--bundle", "unused", "--request", "unused.json"]),
              patch.object(host, "read_json", return_value=request),
              patch.object(host, "verify_bundle", return_value=request),
              patch.object(host, "http", return_value=(json.dumps(metadata).encode(), {}, 200)),
              patch.object(host, "require_private"),
              patch.object(host.shutil, "which", return_value=None),
              patch.object(host, "execute") as execute,
              patch.object(host, "push_images") as push_images):
            with self.assertRaisesRegex(GateError, "must already contain skopeo"):
                host.main()
            execute.assert_not_called()
            push_images.assert_not_called()

    def test_route_readback_uses_real_ip_and_refuses_unbound_or_bypassed_paths(self):
        for bound in (False, True):
            result = network.observe_edge(self.edge_client(bound), self.manifest, self.stage,
                                          bound=bound, identity=self.identity)
            self.assertEqual(result["firewallPrivateIp"], "10.42.6.10")
            self.assertFalse(result["dnsAndTrafficProven"])
            self.assertFalse(result["vpnLoginProven"])
        for field in ("ip", "dns", "propagation", "association", "audience", "public-ip", "zones"):
            client = self.edge_client(True)
            ids = self.stage["edge"]["resources"]
            values = {row["id"]: row["properties"] for row in client.resources.values()}
            if field == "ip":
                values[ids["firewallId"]]["ipConfigurations"][0]["properties"]["privateIPAddress"] = "10.42.6.4"
            elif field == "dns":
                values[self.vnet]["dhcpOptions"]["dnsServers"] = ["168.63.129.16"]
            elif field == "propagation":
                values[self.stage["network"]["gatewayRouteTableId"]]["disableBgpRoutePropagation"] = True
            elif field == "association":
                values[self.vnet]["subnets"][0]["properties"].pop("routeTable")
            elif field == "audience":
                values[ids["gatewayId"]]["vpnClientConfiguration"]["aadAudience"] = network.VPN_CLIENT
            elif field == "public-ip":
                values[ids["gatewayPublicIpId"]]["publicIPAllocationMethod"] = "Dynamic"
            else:
                client.resources[ARM + ids["firewallId"] + "?api-version=2024-07-01"]["zones"] = []
            with self.subTest(field=field), self.assertRaises(GateError):
                network.observe_edge(client, self.manifest, self.stage, bound=True, identity=self.identity)

    def test_dns_readback_rejects_changed_domains_extra_rules_and_foreign_links(self):
        selected = network.normalized_policy(self.manifest, self.stage, self.platform)
        ids = self.stage["edge"]["resources"]
        for bound in (False, True):
            result = network.observe_policy(self.policy_client(selected, bound), self.manifest,
                                            self.stage, selected, bound=bound)
            self.assertFalse(result["dnsAndHttpsTrafficProven"])
            self.assertFalse(result["exactQnameOrPerSourceDnsIsolationClaim"])
        for field in ("domains", "extra-rule", "disabled-deny", "deny-action", "deny-list",
                      "extra-link", "foreign-link", "initial-link"):
            client = self.policy_client(selected, True)
            rules = client.collections[ARM + ids["dnsPolicyId"] + "/dnsSecurityRules?api-version=2025-05-01"]
            links = client.collections[ARM + ids["dnsPolicyId"] + "/virtualNetworkLinks?api-version=2025-05-01"]
            if field == "domains":
                client.resources[ARM + ids["allowedDomainsId"] + "?api-version=2025-05-01"][
                    "properties"]["domains"].append("unexpected.example.invalid")
            elif field == "extra-rule":
                rules.append({**rules[0], "id": ids["allowDnsRuleId"] + "-extra"})
            elif field == "disabled-deny":
                rules[1]["properties"]["dnsSecurityRuleState"] = "Disabled"
            elif field == "deny-action":
                rules[1]["properties"]["action"] = {"actionType": "Allow"}
            elif field == "deny-list":
                rules[1]["properties"]["dnsResolverDomainLists"] = [{"id": ids["allowedDomainsId"]}]
            elif field == "extra-link":
                links.append({**links[0], "id": ids["dnsLinkId"] + "-extra"})
            elif field == "foreign-link":
                links[0]["properties"]["virtualNetwork"]["id"] = self.vnet + "-fabric"
            with self.subTest(field=field), self.assertRaises(GateError):
                network.observe_policy(client, self.manifest, self.stage, selected, bound=field != "initial-link")

    def test_firewall_readback_rejects_scope_protocol_and_private_deny_changes(self):
        selected = network.normalized_policy(self.manifest, self.stage, self.platform, allow_clients=True)
        ids = self.stage["edge"]["resources"]
        for field in ("source", "target", "http", "ip-selector", "extra-rule", "missing-private-deny",
                      "private-allow", "extra-group"):
            client = self.policy_client(selected, True)
            groups = client.collections[
                ARM + ids["firewallPolicyId"] + "/ruleCollectionGroups?api-version=2024-07-01"]
            https = groups[1]["properties"]["ruleCollections"][0]["rules"]
            if field == "source":
                https[-1]["sourceAddresses"] = ["0.0.0.0/0"]
            elif field == "target":
                https[-1]["targetFqdns"] = ["*.example.invalid"]
            elif field == "http":
                https[-1]["protocols"] = [{"protocolType": "Http", "port": 80}]
            elif field == "ip-selector":
                https[-1]["destinationAddresses"] = ["*"]
            elif field == "extra-rule":
                https.append({**https[-1], "name": "unreviewed"})
            elif field == "missing-private-deny":
                groups[0]["properties"]["ruleCollections"] = []
            elif field == "private-allow":
                groups[0]["properties"]["ruleCollections"][0]["action"] = {"type": "Allow"}
            else:
                groups.append({**groups[0], "id": ids["privateDataRulesId"] + "-extra"})
            with self.subTest(field=field), self.assertRaises(GateError):
                network.observe_policy(client, self.manifest, self.stage, selected, bound=True)

    def test_checkpoint_uses_last_completed_policy_not_newer_runtime_endpoints(self):
        selected = network.normalized_policy(self.manifest, self.stage, self.platform)
        checkpoint = {**self.stage, "policyInput": selected}
        outputs = {
            "phase-identities": {"vpn": self.identity},
            "phase-network": {**self.stage, "policyInput": network.normalized_policy(self.manifest)},
            "phase-edge-bind": checkpoint,
            "phase-runtime": {"platform": {"endpoints": {"sqlServer": "later.example.invalid"}}},
        }
        client = self.policy_client(selected, True)
        with (patch.object(bootstrap, "verify_vpn_identity"),
              patch.object(bootstrap, "observe_edge") as edge,
              patch.object(bootstrap, "observe_edge_monitoring")):
            bootstrap.verify_network_checkpoint(client, self.manifest, outputs)
            edge.assert_called_once_with(client, self.manifest, checkpoint, bound=True, identity=self.identity)
            checkpoint.pop("policyInput")
            with self.assertRaisesRegex(GateError, "policy-input binding"):
                bootstrap.verify_network_checkpoint(client, self.manifest, outputs)
            with self.assertRaisesRegex(GateError, "No completed own network checkpoint"):
                bootstrap.verify_network_checkpoint(client, self.manifest, {})

    def test_edge_metrics_refuse_raw_logs_foreign_sinks_and_parallel_settings(self):
        ids = self.stage["edge"]["resources"]
        workspace = self.group + "/providers/Microsoft.OperationalInsights/workspaces/log-test"
        platform = {
            "platform": {"resources": {"workspace": workspace}},
            "edgeMonitoring": {role: ids[role + "Id"] + "/providers/Microsoft.Insights/diagnosticSettings/metrics-only"
                               for role in ("firewall", "gateway")},
        }
        resources = {
            ARM + workspace + "?api-version=2025-02-01": self.resource(
                workspace, "Microsoft.OperationalInsights/workspaces", {}),
        }
        collections = {
            ARM + ids[role + "Id"] + "/providers/Microsoft.Insights/diagnosticSettings?api-version=2021-05-01-preview": [
                {"id": platform["edgeMonitoring"][role], "properties": {
                    "workspaceId": workspace, "logs": [], "metrics": [{"category": "AllMetrics", "enabled": True}],
                }}] for role in ("firewall", "gateway")
        }
        client = ReadOnlyClient(resources, collections)
        result = network.observe_edge_monitoring(client, self.manifest, self.stage, platform)
        self.assertFalse(result["metricIngestionObserved"])
        self.assertFalse(result["networkAlertDeliveryProven"])
        for mutation in (
            lambda rows: rows[0]["properties"].update(logs=[{"category": "P2SDiagnosticLog", "enabled": True}]),
            lambda rows: rows[0]["properties"].update(workspaceId=workspace + "-foreign"),
            lambda rows: rows[0]["properties"].update(eventHubName="unexpected-export"),
            lambda rows: rows[0]["properties"]["metrics"][0].update(enabled=False),
            lambda rows: rows.append(copy.deepcopy(rows[0])),
        ):
            changed = copy.deepcopy(collections)
            mutation(next(iter(changed.values())))
            with self.subTest(mutation=mutation), self.assertRaises(GateError):
                network.observe_edge_monitoring(ReadOnlyClient(resources, changed),
                                                self.manifest, self.stage, platform)

    def test_phase_parameters_never_replay_access_credentials_for_policy_only(self):
        self.manifest["releaseBundle"] = {"deliveryUrl": "https://artifact.example.invalid?sig=private-test-sentinel"}
        self.manifest["controlTokenBindings"] = {"private": "do-not-export-token-binding"}
        outputs = {"phase-identities": {"vpn": self.identity}, "phase-network": self.stage,
                   "phase-edge-bind": self.stage, "phase-foundation": {"platform": self.platform}}
        initial = bootstrap.arm_parameters(self.manifest, "network", outputs)
        self.assertFalse(initial["bindManagedEdge"]["value"])
        self.assertEqual(initial["firewallPrivateIp"]["value"], "")
        bound = bootstrap.arm_parameters(self.manifest, "edge-bind", outputs)
        self.assertEqual(bound["firewallPrivateIp"]["value"], "10.42.6.10")
        policy = bootstrap.arm_parameters(self.manifest, "edge-policy", outputs)
        self.assertTrue(policy["policyOnly"]["value"])
        self.assertEqual(policy["windowsBootstrapPassword"]["value"], "")
        self.assertEqual(policy["vpnAudienceClientId"]["value"], self.identity["clientId"])
        self.assertNotIn("private-test-sentinel", json.dumps(policy))
        self.assertNotIn("do-not-export-token-binding", json.dumps(policy))
        self.assertNotIn("vpnUserObjectIds", policy["profile"]["value"])

    def test_vpn_audience_is_separate_and_four_user_assignment_readback_is_exact(self):
        body = bootstrap.app_body(self.manifest, "vpn")
        self.assertEqual(body["appRoles"], [])
        self.assertEqual(body["requiredResourceAccess"], [])
        self.assertEqual(body["api"]["requestedAccessTokenVersion"], 1)
        scope = bootstrap.stable_id(self.manifest, "scope:p2s_access")
        app = {"id": self.identity["objectId"], "appId": self.identity["clientId"],
               "identifierUris": ["api://" + self.identity["clientId"]], "signInAudience": "AzureADMyOrg",
               "api": body["api"], "appRoles": [], "requiredResourceAccess": [],
               "passwordCredentials": [], "keyCredentials": []}
        principal = {"id": self.identity["servicePrincipalObjectId"], "appId": self.identity["clientId"],
                     "appOwnerOrganizationId": self.manifest["binding"]["tenantId"],
                     "appRoleAssignmentRequired": True, "accountEnabled": True}
        assignments = [{"principalId": value, "principalType": "User",
                        "resourceId": self.identity["servicePrincipalObjectId"],
                        "appRoleId": network.DEFAULT_APP_ROLE} for value in self.manifest["vpnUserObjectIds"]]

        class DirectoryClient:
            def get(self, url):
                return app if "/applications/" in url else principal

            def collection(self, url):
                return assignments

        result = network.verify_vpn_identity(DirectoryClient(), self.manifest, self.identity, scope)
        self.assertEqual(result["assignedUserCount"], 4)
        self.assertFalse(result["tokenAuthenticationProven"])
        principal["appRoleAssignmentRequired"] = False
        with self.assertRaises(GateError):
            network.verify_vpn_identity(DirectoryClient(), self.manifest, self.identity, scope)
        principal["appRoleAssignmentRequired"] = True
        assignments.append({**assignments[0], "principalId": identifier(99)})
        with self.assertRaises(GateError):
            network.verify_vpn_identity(DirectoryClient(), self.manifest, self.identity, scope)

    def test_untagged_policy_needs_exact_tagged_firewall_reference_not_a_name_guess(self):
        ids = self.stage["edge"]["resources"]
        firewall = self.edge_client(False).resources[ARM + ids["firewallId"] + "?api-version=2024-07-01"]
        rows = [firewall, {"id": ids["firewallPolicyId"], "type": "Microsoft.Network/firewallPolicies",
                           "tags": None}]
        collections = {ARM + group + "/resources?api-version=2021-04-01": rows if group == self.group else []
                       for group in bootstrap.own_group_ids(self.manifest)}
        client = ReadOnlyClient({ARM + ids["firewallId"] + "?api-version=2024-07-01": firewall}, collections)
        engine = SimpleNamespace(manifest=self.manifest, client=client, outputs={})
        self.assertIn(ids["firewallPolicyId"].lower(),
                      release.inventory(engine, network_stage=self.stage)[self.group])
        rows.append({"id": ids["firewallPolicyId"] + "-unknown", "type": "Microsoft.Network/firewallPolicies"})
        with self.assertRaises(GateError):
            release.inventory(engine, network_stage=self.stage)

    def test_inventory_preserves_edge_ownership_if_later_network_acceptance_fails(self):
        ids = self.stage["edge"]["resources"]
        firewall_url = ARM + ids["firewallId"] + "?api-version=2024-07-01"
        firewall = self.edge_client(False).resources[firewall_url]
        rows = [firewall, {"id": ids["firewallPolicyId"], "type": "Microsoft.Network/firewallPolicies"}]
        client = ReadOnlyClient({firewall_url: firewall}, {
            ARM + group + "/resources?api-version=2021-04-01": rows if group == self.group else []
            for group in bootstrap.own_group_ids(self.manifest)})
        journal = MagicMock()
        journal.begin.return_value = True
        journal.state = {"events": []}
        engine = SimpleNamespace(manifest=self.manifest, client=client, outputs={},
                                 journal=journal, effect=MagicMock())

        def capture(operation, output):
            engine.outputs[operation] = copy.deepcopy(output)
            journal.state["events"].append({
                "kind": "completed", "operation": operation, "details": {"output": copy.deepcopy(output)}})

        journal.complete.side_effect = capture
        release.checkpoint_inventory(engine, "network", self.stage)
        self.assertNotIn("phase-network", engine.outputs)
        self.assertTrue(release.cleanup(engine)["resourceGroupsAndOwnDirectoryObjectsDeleted"])
        self.assertEqual(engine.effect.call_count, 2)
        engine.effect.reset_mock()
        firewall["properties"]["firewallPolicy"]["id"] += "-foreign"
        with self.assertRaises(GateError):
            release.cleanup(engine)
        engine.effect.assert_not_called()

    def test_cleanup_resume_uses_bound_group_order_not_serialized_dictionary_order(self):
        groups = bootstrap.own_group_ids(self.manifest)
        captured = json.loads(json.dumps({group: [] for group in groups}, sort_keys=True))
        journal = SimpleNamespace(state={"events": [
            {"kind": "completed", "operation": "inventory-network", "details": {"output": captured}}]})
        engine = SimpleNamespace(manifest=self.manifest, outputs={"cleanup-rg-0": {"deletedResourceGroup": groups[0]}},
                                 journal=journal, effect=MagicMock(), client=ReadOnlyClient(collections={
                                     ARM + groups[1] + "/resources?api-version=2021-04-01": []}))
        self.assertTrue(release.cleanup(engine)["resourceGroupsAndOwnDirectoryObjectsDeleted"])
        self.assertEqual(engine.effect.call_args_list[-1].args[2], ARM + groups[1] + "?api-version=2024-03-01")

    def test_d4_readback_requires_unused_consumption_actual_node_and_both_app_bindings(self):
        env_id = self.group + "/providers/Microsoft.App/managedEnvironments/cae-test"
        app_ids = {role: self.group + "/providers/Microsoft.App/containerApps/" + role
                   for role in ("api", "agents")}
        platform = {**self.platform, "resources": {"vnet": self.vnet, "containerEnvironment": env_id},
                    "runtimeResources": app_ids}
        environment = self.resource(env_id, "Microsoft.App/managedEnvironments", {
            "publicNetworkAccess": "Disabled", "defaultDomain": platform["containerEnvironmentDomain"],
            "vnetConfiguration": {"internal": True, "infrastructureSubnetId": self.vnet + "/subnets/snet-container-apps"},
            "workloadProfiles": [{"name": "Consumption", "workloadProfileType": "Consumption"},
                                 {"name": "D4", "workloadProfileType": "D4", "minimumCount": 1, "maximumCount": 1}]})
        states = [{"name": "D4", "properties": {"currentCount": 1, "minimumCount": 1, "maximumCount": 1}}]
        apps = [self.resource(value, "Microsoft.App/containerApps",
                              {"environmentId": env_id, "workloadProfileName": "D4",
                               "configuration": {"activeRevisionsMode": "Single", "dapr": None},
                               "template": {"scale": {"minReplicas": 1, "maxReplicas": 1},
                                            "containers": [{"name": "host", "resources": {"cpu": 0.5, "memory": "1Gi"}}]}})
                for value in app_ids.values()]
        client = ReadOnlyClient(
            {ARM + env_id + "?api-version=2025-07-01": environment},
            {ARM + env_id + "/workloadProfileStates?api-version=2025-07-01": states,
             ARM + self.group + "/providers/Microsoft.App/containerApps?api-version=2025-07-01": apps})
        result = network.observe_container_profiles(client, self.manifest, {"platform": platform}, runtime=True)
        self.assertTrue(result["bothApplicationsObservedOnD4"])
        self.assertTrue(result["bothApplicationsObservedAtSingleReplicaBounds"])
        self.assertFalse(result["rolloutCapacityProven"])
        for mutation in (
            lambda value: value["template"]["scale"].update(maxReplicas=3),
            lambda value: value["template"]["scale"].update(minReplicas=0),
            lambda value: value["template"]["scale"].update(maxReplicas=True),
            lambda value: value["configuration"].update(activeRevisionsMode="Multiple"),
            lambda value: value["template"]["containers"].append({"name": "unreviewed-sidecar"}),
            lambda value: value["template"].update(initContainers=[{"name": "unreviewed-init"}]),
            lambda value: value["configuration"].update(dapr={"enabled": True}),
            lambda value: value["template"]["containers"][0]["resources"].update(cpu=1),
            lambda value: value["template"]["containers"][0]["resources"].update(memory="2Gi"),
        ):
            original = copy.deepcopy(apps[0]["properties"])
            mutation(apps[0]["properties"])
            with self.subTest(mutation=mutation), self.assertRaises(GateError):
                network.observe_container_profiles(client, self.manifest, {"platform": platform}, runtime=True)
            apps[0]["properties"] = original
        for wrong in (0, 2, True):
            states[0]["properties"]["currentCount"] = wrong
            with self.subTest(nodeCount=wrong), self.assertRaises(GateError):
                network.observe_container_profiles(client, self.manifest, {"platform": platform}, runtime=True)
        states[0]["properties"]["currentCount"] = 1
        apps[0]["properties"].pop("workloadProfileName")
        with self.assertRaises(GateError):
            network.observe_container_profiles(client, self.manifest, {"platform": platform}, runtime=True)
        apps[0]["properties"]["workloadProfileName"] = "D4"
        environment["properties"]["workloadProfiles"].pop(0)
        with self.assertRaises(GateError):
            network.observe_container_profiles(client, self.manifest, {"platform": platform}, runtime=True)


if __name__ == "__main__":
    unittest.main()
