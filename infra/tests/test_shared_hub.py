import copy
from contextlib import contextmanager
import datetime as dt
from pathlib import Path
import subprocess
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import MagicMock, patch


sys.path.insert(0, str(Path(__file__).parents[1] / "scripts"))
import shared_hub as hub
import shared_hub_control as controller
import primary_bootstrap as bootstrap
from primary_control import ARM, GateError, Journal, digest, file_hash, write_json


SUBSCRIPTION = "11111111-2222-4333-8444-555555555555"
GENERATION = "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"
GROUP = f"/subscriptions/{SUBSCRIPTION}/resourceGroups/rg-testhub-hub"


def manifest():
    return {
        "contract": hub.HUB_CONTRACT, "deploymentId": GENERATION, "generation": GENERATION,
        "bindingHash": "1" * 64, "binding": {"subscriptionId": SUBSCRIPTION},
        "namePrefix": "testhub", "names": {"hubResourceGroup": "rg-testhub-hub"},
        "location": "francecentral", "createdAtUtc": "2026-11-18T00:00:00Z",
        "expiresAtUtc": "2026-11-30T00:00:00Z", "consumerSlots": ["primary", "fabric"],
        # Test-only allocations. These are never copied into deployment templates or receipts.
        "network": {"vnet": "10.80.0.0/16", "firewall": "10.80.0.0/26", "gateway": "10.80.0.64/27",
                    "waf": "10.80.1.0/24", "endpoints": "10.80.2.0/27",
                    "vpnClientPool": "172.28.240.0/24", "zones": ["1"]},
        "waf": {"sku": "WAF_v2", "frontendPrivateIp": "10.80.1.10", "instanceCount": 1},
        "vpnUserObjectIds": [f"00000000-0000-4000-8000-00000000000{i}" for i in range(1, 5)],
        "resolvedUsersEvidenceSha256": "2" * 64, "addressReviewSha256": "3" * 64,
        "capacityReviewSha256": "4" * 64, "budgetBindingSha256": "5" * 64,
        "pilotBoundary": {"createdAtUtc": "2026-11-18T00:00:00Z", "expiresAtUtc": "2026-11-30T00:00:00Z",
                          "budgetBindingSha256": "5" * 64, "sourceEvidenceSha256": "6" * 64},
        "dnsZones": ["privatelink.vaultcore.azure.net", "privatelink.blob.core.windows.net"],
        "edgePolicy": {"dnsDomains": [], "httpsRules": []},
    }


def consumer(key="primary"):
    group = f"/subscriptions/{SUBSCRIPTION}/resourceGroups/rg-{key}-workload"
    vnet = group + f"/providers/Microsoft.Network/virtualNetworks/vnet-{key}-spoke"
    roles = hub.PRIMARY_SUBNETS if key == "primary" else hub.FABRIC_SUBNETS
    octet = 81 if key == "primary" else 82
    return {
        "bindingHash": ("a" if key == "primary" else "b") * 64,
        "deploymentId": f"00000000-0000-4000-8000-00000000000{1 if key == 'primary' else 2}",
        "sourceCommit": "d" * 40, "expiresAtUtc": "2026-11-29T00:00:00Z", "state": "reserved",
        "spoke": {"resourceGroupId": group, "vnetId": vnet, "location": "francecentral",
                  "addressPrefix": f"10.{octet}.0.0/16",
                  "subnets": {role: {"id": vnet + "/subnets/" + role, "addressPrefix": f"10.{octet}.{i}.0/26"}
                              for i, role in enumerate(roles)}},
        "edgePolicy": {"dnsDomains": [], "httpsRules": []},
        "privateEndpoints": [], "dnsRecords": [], "operatorEndpoints": [], "web": None, "offboarding": None,
    }


def update(previous=None, key="primary", entry=None):
    entries = copy.deepcopy(previous["consumers"]) if previous else {"primary": None, "fabric": None}
    entries[key] = consumer(key) if entry is None else entry
    return {"contract": hub.BINDINGS_CONTRACT, "generation": GENERATION,
            "revision": previous["revision"] + 1 if previous else 1,
            "previousSha256": digest(previous) if previous else None, "changedConsumer": key, "consumers": entries}


def web(entry):
    return {
        "frontendHost": "primary.pilot.example.test", "frontendZone": "pilot.example.test", "frontendRecord": "primary",
        "certificateSecretId": "https://hubcert.vault.azure.net/secrets/frontend/" + "a" * 32,
        "certificateEvidenceSha256": "a" * 64, "backendFqdn": "api.internal.azurecontainerapps.io",
        "backendResourceId": entry["spoke"]["resourceGroupId"] + "/providers/Microsoft.App/containerApps/api",
        "backendPrivateIp": "10.81.0.10", "backendRegion": "francecentral", "backendKind": "ContainerAppInternal",
        "backendRootCertificate": None, "probe": {"host": "api.internal.azurecontainerapps.io",
                                                 "path": "/health/ready", "port": 443, "statusCodes": ["200"]},
        "limits": {"bodyKb": 128, "uploadMb": 10, "requestTimeoutSeconds": 60, "drainSeconds": 90, "streaming": "none"},
        "applicationContractSha256": "b" * 64, "forwardedHeadersEvidenceSha256": "c" * 64,
        "privateDnsEvidenceSha256": "d" * 64,
    }


@contextmanager
def waf_offboarding(keep_primary):
    from shared_hub_fixture import resource, synthetic_waf

    owner, previous = synthetic_waf()
    gateway_id = previous["hub"]["wafId"]
    gateway_url = ARM + gateway_id + "?api-version=" + hub.WAF_API
    gateway = owner.client.resources[gateway_url]
    primary = previous["bindings"]["web"][0]
    fabric = consumer("fabric")
    fabric.update(state="web-bound", web=web(fabric))
    spec = fabric["web"]
    spec.update(frontendHost="fabric.pilot.example.test", frontendRecord="fabric",
                certificateSecretId=previous["hub"]["ownerDependencies"]["certificateVaultUri"] + "secrets/fabric/" + "a" * 32,
                backendPrivateIp="10.82.2.10", backendKind="PrivateEndpointWeb")
    policy_id = primary["policyId"].rsplit("/", 1)[0] + "/fabric"
    policy_url = ARM + policy_id + "?api-version=" + hub.NETWORK_API
    grant_id = primary["certificateGrantId"].replace("/secrets/frontend/", "/secrets/fabric/")[:-1] + "4"
    primary_policy = owner.client.resources[ARM + primary["policyId"] + "?api-version=" + hub.NETWORK_API]
    owner.client.add(resource(policy_id, primary_policy["type"], copy.deepcopy(primary_policy["properties"]),
                              tags=copy.deepcopy(primary_policy["tags"])))
    primary_grant = owner.client.resources[ARM + primary["certificateGrantId"] + "?api-version=2022-04-01"]
    owner.client.add(resource(grant_id, primary_grant["type"], primary_grant["properties"]), "2022-04-01")
    overrides = {
        "sslCertificates": {"keyVaultSecretId": spec["certificateSecretId"]},
        "httpListeners": {"hostNames": [spec["frontendHost"]], "firewallPolicy": {"id": policy_id}},
        "backendAddressPools": {"backendAddresses": [{"ipAddress": spec["backendPrivateIp"]}]},
        "backendHttpSettingsCollection": {"probe": {"id": gateway_id + "/probes/ready-fabric"}},
        "probes": {},
        "requestRoutingRules": {"priority": 200, "httpListener": {"id": gateway_id + "/httpListeners/https-fabric"},
                               "backendAddressPool": {"id": gateway_id + "/backendAddressPools/backend-fabric"},
                               "backendHttpSettings": {"id": gateway_id + "/backendHttpSettingsCollection/https-fabric"}},
    }
    for collection, properties in overrides.items():
        item = copy.deepcopy(gateway["properties"][collection][0])
        item["name"] = item["name"].replace("-primary", "-fabric")
        item["properties"].update(properties)
        gateway["properties"][collection] = (gateway["properties"][collection] if keep_primary else []) + [item]
    previous["bindingInput"]["consumers"].update(fabric=fabric)
    if not keep_primary:
        previous["bindingInput"]["consumers"]["primary"] = None
        del owner.client.resources[ARM + primary["policyId"] + "?api-version=" + hub.NETWORK_API]
        del owner.client.resources[ARM + primary["certificateGrantId"] + "?api-version=2022-04-01"]
    # This fixture isolates WAF lifecycle; existing hub/consumer/DNS tests retain their own readbacks.
    previous["bindings"] = {"peers": [], "dnsLinks": [], "dataRecords": [], "frontendRecords": [],
                            "web": ([primary] if keep_primary else []) + [
                                {"consumer": "fabric", "policyId": policy_id, "certificateGrantId": grant_id}]}
    for reference in previous["bindings"]["web"]:
        value = owner.client.resources[ARM + reference["policyId"] + "?api-version=" + hub.NETWORK_API]
        value["properties"].update(applicationGateways=[{"id": gateway_id}],
                                   httpListeners=[{"id": gateway_id + "/httpListeners/https-" + reference["consumer"]}],
                                   pathBasedRules=[])
    observer = controller.Observation(owner.client, [GROUP])
    controller.observe_waf(owner, observer, previous)
    previous["metadataReadback"]["resources"] = observer.bindings()
    retired = copy.deepcopy(fabric)
    retired.update(state="offboarded", web=None, offboarding={
        "stoppedWorkEvidenceSha256": "a" * 64, "credentialsAndGrantsRevokedEvidenceSha256": "b" * 64,
        "spokePeeringAbsentEvidenceSha256": "c" * 64})
    bindings = update(previous["bindingInput"], "fabric", retired)
    plan = {"phase": "hub-bindings"}
    owner.outputs["phase-hub-bindings-000004"] = previous
    controller.prepare_plan(plan, owner.manifest, owner.outputs, bindings)
    result = copy.deepcopy(previous)
    result["bindingInput"] = bindings
    result["bindings"]["web"] = [primary] if keep_primary else []
    if not keep_primary:
        result["hub"].update(wafId=None, wafPrivateIp=None)
    state = SimpleNamespace(owner=owner, previous=previous, plan=plan, result=result, gateway_url=gateway_url,
                            policy_id=policy_id, policy_url=policy_url, deletes=[], puts=[], absent=[],
                            hold_absence=set(), race_etag=False, delete_attempts=[])
    real_read = owner.client.request

    def detach_policy():
        value = owner.client.resources[policy_url]
        value["etag"] = 'W/"synthetic-after-approved-detach"'
        value["properties"].update(applicationGateways=[], httpListeners=[], pathBasedRules=[])

    def request(method, url, body=None, *, allowed=(200,), headers=None):
        if method == "PUT":
            assert "/providers/Microsoft.Resources/deployments/testhub-hub-bindings-000005?" in url
            assert body["properties"]["parameters"]["hubBindings"]["value"] == bindings
            state.puts.append(url)
            if keep_primary:
                for collection in overrides:
                    gateway["properties"][collection] = [item for item in gateway["properties"][collection]
                                                         if not item["name"].endswith("-fabric")]
                gateway["etag"] = 'W/"synthetic-after-approved-detach"'
                detach_policy()
            owner.client.resources[url] = {"properties": {"provisioningState": "Succeeded",
                "parameters": body["properties"]["parameters"], "outputs": {key: {"value": value} for key, value in result.items()}}}
            return {}, {}, 200
        if method == "DELETE":
            assert url in {gateway_url, policy_url, ARM + grant_id + "?api-version=2022-04-01"}
            state.delete_attempts.append(url)
            if url == policy_url and state.race_etag:
                owner.client.resources[url]["etag"] = 'W/"synthetic-concurrent-provider-write"'
            if headers != {"If-Match": owner.client.resources[url]["etag"]}:
                raise GateError("Synthetic HTTP 412; no automatic retry.")
            state.deletes.append((url, headers))
            if url == gateway_url:
                detach_policy()
            if url not in state.hold_absence:
                del owner.client.resources[url]
            return {}, {}, 204
        response = real_read(method, url, body, allowed=allowed, headers=headers)
        if response[2] == 404:
            state.absent.append(url)
        return response

    with tempfile.TemporaryDirectory() as temporary:
        root = Path(temporary)
        state.journal_path = root / "journal.json"
        template = {"resources": []}
        preview = root / "preview.json"
        write_json(preview, {"planHash": digest(plan), "compiledTemplateHash": digest(template),
                             "result": {"status": "Succeeded", "changes": []}})

        def apply(engine):
            return bootstrap.arm_apply(engine, "hub-bindings", plan, {"whatIfSha256": file_hash(preview)}, preview)

        state.apply = apply
        with Journal(state.journal_path, owner.manifest["bindingHash"]) as journal:
            for operation, value in owner.outputs.items():
                journal.begin(operation, "LOCAL-CHECKPOINT", "synthetic-waf-fixture", {})
                journal.complete(operation, value)
        with (patch.object(owner.client, "request", side_effect=request),
              patch.object(bootstrap, "check_owned_groups"), patch.object(bootstrap, "compile_bicep", return_value=template),
              patch("primary_release.checkpoint_inventory"),
              patch.object(controller, "observe_hub", side_effect=lambda engine, stage, **_: controller.Observation(engine.client, [GROUP])),
              patch.object(controller, "observe_consumer"),
              patch.object(controller, "observe_binding_resources",
                           side_effect=lambda engine, observer, stage: controller.observe_waf(engine, observer, stage) if stage["hub"]["wafId"] else None)):
            yield state


class SharedHubContractTests(unittest.TestCase):
    def test_consumers_cannot_share_a_certificate_grant_scope_under_separate_lifecycles(self):
        previous = update(update(), "fabric")
        previous["consumers"]["primary"].update(state="web-bound", web=web(previous["consumers"]["primary"]))
        previous["consumers"]["fabric"]["state"] = "data-bound"
        fabric = copy.deepcopy(previous["consumers"]["fabric"])
        fabric.update(state="web-bound", web=web(fabric))
        fabric["web"].update(frontendHost="fabric.pilot.example.test", frontendRecord="fabric",
                             backendPrivateIp="10.82.2.10", backendKind="PrivateEndpointWeb")
        original_secret = fabric["web"]["certificateSecretId"]
        for secret in (original_secret, original_secret.replace("/frontend/", "/Frontend/").replace("a" * 32, "b" * 32)):
            fabric["web"]["certificateSecretId"] = secret
            with self.subTest(secret=secret), self.assertRaisesRegex(GateError, "distinct certificate-secret scope"):
                hub.validate_bindings(update(previous, "fabric", fabric), manifest(), previous)
        fabric["web"]["certificateSecretId"] = original_secret
        fabric["web"]["certificateSecretId"] = fabric["web"]["certificateSecretId"].replace("/frontend/", "/fabric-frontend/")
        hub.validate_bindings(update(previous, "fabric", fabric), manifest(), previous)

    def test_explicit_hub_profile_rejects_null_allocations_legacy_owners_and_unbounded_waf(self):
        baseline = manifest()
        hub.validate_hub_manifest(baseline)
        for field, value in (("generation", "00000000-0000-4000-8000-000000000000"),
                             ("consumerSlots", ["primary"]), ("resolvedUsersEvidenceSha256", None)):
            changed = copy.deepcopy(baseline)
            changed[field] = value
            with self.subTest(field=field), self.assertRaises(GateError):
                hub.validate_hub_manifest(changed)
        for key, value in (("vnet", None), ("gateway", "10.80.0.0/27"),
                           ("waf", "10.80.1.0/27"), ("vpnClientPool", "10.80.3.0/24")):
            changed = copy.deepcopy(baseline)
            changed["network"][key] = value
            with self.subTest(key=key), self.assertRaises(GateError):
                hub.validate_hub_manifest(changed)
        changed = copy.deepcopy(baseline)
        changed["waf"]["instanceCount"] = True
        with self.assertRaises(GateError):
            hub.validate_hub_manifest(changed)

    def test_two_consumer_revisions_do_not_adopt_or_rewrite_the_other(self):
        first = update()
        hub.validate_bindings(first, manifest())
        second = update(first, "fabric")
        hub.validate_bindings(second, manifest(), first)
        changed = copy.deepcopy(second)
        changed["consumers"]["primary"]["edgePolicy"]["dnsDomains"] = ["unexpected.example.test"]
        with self.assertRaisesRegex(GateError, "other consumer"):
            hub.validate_bindings(changed, manifest(), first)
        changed = update(second)
        changed["consumers"]["fabric"] = None
        with self.assertRaisesRegex(GateError, "other consumer"):
            hub.validate_bindings(changed, manifest(), second)
        for field, value in (("generation", SUBSCRIPTION), ("revision", 2), ("previousSha256", "0" * 64)):
            changed = update(second)
            changed[field] = value
            with self.subTest(field=field), self.assertRaises(GateError):
                hub.validate_bindings(changed, manifest(), second)

    def test_region_address_source_and_deadline_changes_do_not_silently_adopt(self):
        first = update()
        second = update(first, "fabric")
        second["consumers"]["fabric"]["spoke"]["addressPrefix"] = first["consumers"]["primary"]["spoke"]["addressPrefix"]
        with self.assertRaises(GateError):
            hub.validate_bindings(second, manifest(), first)
        for key, value in (("sourceCommit", "e" * 40), ("expiresAtUtc", "2026-12-01T00:00:00Z"),
                           ("bindingHash", "e" * 64)):
            changed = update(first)
            changed["consumers"]["primary"][key] = value
            with self.subTest(key=key), self.assertRaises(GateError):
                hub.validate_bindings(changed, manifest(), first)
        direct = update()
        direct["consumers"]["primary"]["state"] = "connected"
        with self.assertRaisesRegex(GateError, "reservation"):
            hub.validate_bindings(direct, manifest())

    def test_waf_requires_real_separate_https_contract_not_hosted_sse_or_tls_bypass(self):
        entry = consumer()
        baseline = web(entry)
        hub.validate_web(baseline, "primary", entry["spoke"], manifest())
        for key, value in (("backendKind", "FabricC1Hosted"), ("backendRegion", "swedencentral"),
                           ("frontendHost", "*.pilot.example.test"), ("forwardedHeadersEvidenceSha256", None),
                           ("certificateSecretId", "https://hubcert.vault.azure.net/secrets/frontend"),
                           ("backendPrivateIp", "203.0.113.1")):
            changed = copy.deepcopy(baseline)
            changed[key] = value
            with self.subTest(key=key), self.assertRaises(GateError):
                hub.validate_web(changed, "primary", entry["spoke"], manifest())
        for key, value in (("streaming", "sse"), ("bodyKb", 129), ("requestTimeoutSeconds", True),
                           ("drainSeconds", 1)):
            changed = copy.deepcopy(baseline)
            changed["limits"][key] = value
            with self.subTest(key=key), self.assertRaises(GateError):
                hub.validate_web(changed, "primary", entry["spoke"], manifest())

    def test_offboarding_requires_stop_credentials_and_exact_reference_removal(self):
        first = update()
        second = update(first, "fabric")
        entry = copy.deepcopy(second["consumers"]["primary"])
        entry["state"] = "offboarded"
        changed = update(second, entry=entry)
        with self.assertRaises(GateError):
            hub.validate_bindings(changed, manifest(), second)
        changed["consumers"]["primary"]["offboarding"] = {
            "stoppedWorkEvidenceSha256": "a" * 64, "credentialsAndGrantsRevokedEvidenceSha256": "b" * 64,
            "spokePeeringAbsentEvidenceSha256": "c" * 64,
        }
        hub.validate_bindings(changed, manifest(), second)
        self.assertEqual(changed["consumers"]["fabric"], second["consumers"]["fabric"])
        resurrect = update(changed)
        with self.assertRaises(GateError):
            hub.validate_bindings(resurrect, manifest(), changed)
        remaining = copy.deepcopy(changed["consumers"]["fabric"])
        remaining["state"] = "peered"
        hub.validate_bindings(update(changed, "fabric", remaining), manifest(), changed)

    def test_primary_cannot_import_legacy_network_or_gain_operator_pe_rights(self):
        old = {"contract": "cohors-primary-bootstrap-2", "edge": {"firewallPrivateIp": "10.80.0.4"}}
        with self.assertRaises(GateError):
            hub.validate_receipt(old, {})
        first = update()
        for state in ("peered", "connected"):
            entry = copy.deepcopy(first["consumers"]["primary"])
            entry["state"] = state
            first = update(first, entry=entry)
        connected = copy.deepcopy(first["consumers"]["primary"])
        connected["state"] = "data-bound"
        connected["operatorEndpoints"] = [{"port": 443}]
        with self.assertRaisesRegex(GateError, "Primary does not grant"):
            hub.validate_bindings(update(first, entry=connected), manifest(), first)

    def test_policy_is_source_scoped_and_never_introduces_spoke_transit(self):
        entry = consumer()
        entry["edgePolicy"] = {"dnsDomains": ["login.microsoftonline.com"], "httpsRules": [
            {"name": "identity", "sourceSegments": ["containerApps"], "targetFqdns": ["login.microsoftonline.com"]}]}
        first = update(entry=entry)
        entry = copy.deepcopy(entry)
        entry["state"] = "peered"
        bound = update(first, entry=entry)
        hub.validate_bindings(bound, manifest(), first)
        selected = hub.hub_policy(manifest(), bound)
        self.assertEqual(selected["httpsRules"], [{
            "name": "primary-identity", "sourceAddresses": ["10.81.0.0/26"],
            "targetFqdns": ["login.microsoftonline.com"]}])
        entry["edgePolicy"]["httpsRules"][0]["sourceSegments"] = ["hubWaf"]
        with self.assertRaises(GateError):
            hub.validate_bindings(update(first, entry=entry), manifest(), first)

    def test_arm_reference_parser_rejects_cross_scope_queries_and_traversal(self):
        own = GROUP + "/providers/Microsoft.Network/virtualNetworks/test"
        hub.arm_id(own, scope=GROUP, kind="Microsoft.Network/virtualNetworks")
        for identifier in (own + "?api-version=x", own + "/../test", own.replace("rg-testhub-hub", "rg-foreign"),
                           "https://management.azure.com" + own):
            with self.subTest(identifier=identifier), self.assertRaises(GateError):
                hub.arm_id(identifier, scope=GROUP, kind="Microsoft.Network/virtualNetworks")


class SharedHubReadbackTests(unittest.TestCase):
    def test_actual_collector_emits_each_handshake_state_with_fabric_owned_tags(self):
        from shared_hub_fixture import collect, NOW
        for state in ("reserved", "peered", "connected", "data-bound"):
            with self.subTest(state=state):
                engine, stage, observer, receipt = collect("fabric", state)
                hub.validate_wire_receipt(receipt, now=NOW)
                hub.verify_observations(engine.client, receipt)
                self.assertEqual(receipt["egress"]["httpsRules"][0]["name"], "fabric-identity")
                self.assertEqual(receipt["vpn"]["clientPool"], engine.manifest["network"]["vpnClientPool"])
                self.assertNotIn("ownerDependencies", receipt["hub"])
                self.assertTrue(all("/rg-primary-" not in item["id"] for item in receipt["readback"]["resources"]))
                self.assertTrue(all("/rg-primary-" not in url for _, url in engine.client.calls))
                if state == "peered":
                    self.assertFalse(receipt["peering"]["spokeToHub"]["useRemoteGateways"])
                if state == "connected":
                    self.assertTrue(receipt["peering"]["spokeToHub"]["useRemoteGateways"])

    def test_reserved_policy_is_desired_not_an_installed_firewall_allow(self):
        from shared_hub_fixture import collect
        engine, _, _, receipt = collect("fabric", "reserved")
        rules_id = receipt["hub"]["firewallPolicyId"] + "/ruleCollectionGroups/reviewed-https"
        actual = engine.client.resources[ARM + rules_id + "?api-version=" + hub.NETWORK_API]["properties"]
        self.assertEqual(actual["ruleCollections"], [])
        self.assertTrue(receipt["egress"]["httpsRules"])
        hub.verify_observations(engine.client, receipt)

    def test_manifest_owner_is_opaque_and_primary_tags_cannot_adopt_fabric(self):
        from shared_hub_fixture import synthetic_stage
        for tags in ({"cohorsBinding": "b" * 64}, {"deploymentId": GENERATION, "manifestSha256": "b" * 64},
                     {"deploymentId": "00000000-0000-4000-8000-000000000002", "manifestSha256": "c" * 64}):
            engine, stage = synthetic_stage("fabric")
            entry = {"key": "fabric", **stage["bindingInput"]["consumers"]["fabric"]}
            engine.client.resources[ARM + entry["spoke"]["vnetId"] + "?api-version=" + hub.NETWORK_API]["tags"] = tags
            observation = controller.Observation(engine.client, [stage["hub"]["resourceGroupId"], entry["spoke"]["resourceGroupId"]])
            with self.subTest(tags=tags), self.assertRaisesRegex(GateError, "owned"):
                controller.observe_consumer(engine, observation, entry, stage["hub"], post_apply=True)

    def test_rehashed_receipt_cannot_substitute_actual_pool_firewall_or_transit(self):
        from shared_hub_fixture import collect
        for mutate in (
            lambda value: value["vpn"].update(clientPool="172.29.240.0/24"),
            lambda value: value["hub"].update(firewallPrivateIp="10.80.0.11", dnsServerIp="10.80.0.11"),
            lambda value: value["peering"]["spokeToHub"].update(useRemoteGateways=False),
        ):
            engine, _, _, receipt = collect()
            mutate(receipt)
            receipt["receiptSha256"] = digest({key: value for key, value in receipt.items() if key != "receiptSha256"})
            with self.assertRaises(GateError):
                hub.verify_observations(engine.client, receipt)

    def test_foreign_readback_is_rejected_before_any_arm_request(self):
        from shared_hub_fixture import collect
        engine, _, _, receipt = collect()
        receipt["readback"]["resources"].append({
            "id": GROUP.replace("rg-testhub-hub", "rg-primary-workload") + "/providers/Microsoft.Network/virtualNetworks/foreign",
            "apiVersion": hub.NETWORK_API, "sha256": "f" * 64, "etag": None})
        engine.client.calls.clear()
        with self.assertRaisesRegex(GateError, "unbound"):
            hub.verify_observations(engine.client, receipt)
        self.assertEqual(engine.client.calls, [])

    def test_owner_emitter_filters_other_consumer_observations(self):
        from shared_hub_fixture import collect, resource, NOW, OBSERVED
        engine, stage, observer, _ = collect()
        foreign = GROUP.replace("rg-testhub-hub", "rg-primary-workload") + "/providers/Microsoft.Network/virtualNetworks/foreign"
        observer.values[foreign], observer.versions[foreign] = resource(foreign, "Microsoft.Network/virtualNetworks", {}), hub.NETWORK_API
        with patch.object(controller, "timestamp", return_value=OBSERVED), patch.object(hub, "utcnow", return_value=NOW):
            receipt = controller.make_receipt(engine, "fabric", stage, observer)
        self.assertNotIn(foreign, [item["id"] for item in receipt["readback"]["resources"]])

    def test_public_dns_broad_vnet_nsg_and_asymmetric_hub_routes_fail(self):
        from shared_hub_fixture import synthetic_stage, rule
        for mutation in ("dns", "nsg", "route"):
            engine, stage = synthetic_stage()
            entry = {"key": "fabric", **stage["bindingInput"]["consumers"]["fabric"]}
            vnet = engine.client.resources[ARM + entry["spoke"]["vnetId"] + "?api-version=" + hub.NETWORK_API]["properties"]
            if mutation == "dns":
                vnet["dhcpOptions"]["dnsServers"] = ["168.63.129.16"]
            elif mutation == "nsg":
                identifier = vnet["subnets"][0]["properties"]["networkSecurityGroup"]["id"]
                engine.client.resources[ARM + identifier + "?api-version=" + hub.NETWORK_API]["properties"]["securityRules"].append(
                    rule("broad-vnet", "Inbound", "Allow", "VirtualNetwork", "*", "*", 50))
            else:
                identifier = vnet["subnets"][0]["properties"]["routeTable"]["id"]
                engine.client.resources[ARM + identifier + "?api-version=" + hub.NETWORK_API]["properties"]["routes"].append(
                    {"properties": {"addressPrefix": stage["hub"]["addressPrefix"], "nextHopType": "VirtualAppliance",
                                    "nextHopIpAddress": stage["hub"]["firewallPrivateIp"]}})
            observation = controller.Observation(engine.client, [stage["hub"]["resourceGroupId"], entry["spoke"]["resourceGroupId"]])
            with self.subTest(mutation=mutation), self.assertRaises(GateError):
                controller.observe_consumer(engine, observation, entry, stage["hub"], post_apply=True)

    def test_endpoint_approval_nic_and_fqdn_must_be_actual_not_claimed(self):
        from shared_hub_fixture import synthetic_stage
        for field in ("approval", "nic", "fqdn"):
            engine, stage = synthetic_stage("fabric", "data-bound")
            entry = {"key": "fabric", **stage["bindingInput"]["consumers"]["fabric"]}
            endpoint = entry["privateEndpoints"][0]
            actual = engine.client.resources[ARM + endpoint["id"] + "?api-version=" + hub.NETWORK_API]["properties"]
            if field == "approval":
                actual["privateLinkServiceConnections"][0]["properties"]["privateLinkServiceConnectionState"]["status"] = "Pending"
            elif field == "nic":
                identifier = actual["networkInterfaces"][0]["id"]
                engine.client.resources[ARM + identifier + "?api-version=" + hub.NETWORK_API]["properties"]["ipConfigurations"][0]["properties"]["privateIPAddress"] = "10.81.0.10"
            else:
                entry["operatorEndpoints"][0]["fqdns"] = ["unobserved.example.test"]
            observation = controller.Observation(engine.client, [stage["hub"]["resourceGroupId"], entry["spoke"]["resourceGroupId"]])
            with self.subTest(field=field), self.assertRaises(GateError):
                controller.observe_consumer(engine, observation, entry, stage["hub"], post_apply=True)

    def test_missing_fields_and_false_acceptance_do_not_pass_a_rehashed_wire_receipt(self):
        from shared_hub_fixture import collect, NOW
        for mutate in (lambda value: value["vpn"].pop("clientPool"),
                       lambda value: value["readback"].update(trafficAccepted=True),
                       lambda value: value["hub"].update(ownerDependencies={}),
                       lambda value: value.update(revision=True)):
            _, _, _, receipt = collect()
            mutate(receipt)
            receipt["receiptSha256"] = digest({key: value for key, value in receipt.items() if key != "receiptSha256"})
            with self.assertRaises(GateError):
                hub.validate_wire_receipt(receipt, now=NOW)

    def test_terminal_offboarding_receipt_can_release_after_original_deadline_not_activate(self):
        from shared_hub_fixture import collect
        _, _, _, receipt = collect("fabric", "reserved")
        receipt.update(state="offboarded")
        receipt["egress"]["httpsRules"] = []
        receipt["readback"]["observedAtUtc"] = "2026-12-01T10:00:00Z"
        receipt["receiptSha256"] = digest({key: value for key, value in receipt.items() if key != "receiptSha256"})
        now = dt.datetime(2026, 12, 1, 11, tzinfo=dt.timezone.utc)
        hub.validate_wire_receipt(receipt, now=now)
        with self.assertRaisesRegex(GateError, "offboarded"):
            hub.latest_receipt({"hub-receipt-4": receipt}, required_state="connected")

    def test_plan_revisions_and_offboarding_preserve_other_consumer_references(self):
        first = update()
        previous = update(first, "fabric")
        previous["consumers"]["primary"]["state"] = "web-bound"
        previous["consumers"]["primary"]["web"] = web(previous["consumers"]["primary"])
        previous["consumers"]["fabric"]["state"] = "connected"
        candidate = copy.deepcopy(previous["consumers"]["fabric"])
        candidate.update(state="offboarded", offboarding={
            "stoppedWorkEvidenceSha256": "a" * 64, "credentialsAndGrantsRevokedEvidenceSha256": "b" * 64,
            "spokePeeringAbsentEvidenceSha256": "c" * 64})
        next_revision = update(previous, "fabric", candidate)
        owner_stage = {"bindingInput": previous, "hub": {"wafId": GROUP + "/providers/Microsoft.Network/applicationGateways/shared"},
                       "bindings": {"peers": [{"consumer": "primary", "id": "keep-primary"}, {"consumer": "fabric", "id": "release-fabric"}]}}
        outputs = {"phase-edge-bind": {}, "phase-hub-bindings-000002": owner_stage}
        plan = {"phase": "hub-bindings"}
        controller.prepare_plan(plan, manifest(), outputs, next_revision)
        self.assertEqual(plan["operationPhase"], "hub-bindings-000003")
        self.assertEqual(plan["offboardReferences"], ["release-fabric"])
        self.assertIsNone(plan["deleteUnreferencedWaf"])
        with self.assertRaisesRegex(GateError, "either consumer"):
            controller.require_retirement(SimpleNamespace(outputs=outputs))

    def test_primary_redirect_uses_private_frontend_origin_not_backend_sni(self):
        owner = {"contract": hub.PRIMARY_CONTRACT, "deploymentId": GENERATION, "bindingHash": "b" * 64}
        engine = SimpleNamespace(manifest=owner, outputs={"phase-identities": {"spa": {"objectId": GENERATION}}},
                                 client=MagicMock(), effect=MagicMock())
        engine.client.get.side_effect = [
            {"tags": bootstrap.ownership_tags(owner, "spa")},
            {"spa": {"redirectUris": ["https://primary.pilot.example.test"]}},
        ]
        with patch.object(controller, "require_primary_phase_receipt", return_value={"web": {
                "frontendHost": "primary.pilot.example.test", "backendFqdn": "api.internal.azurecontainerapps.io"}}):
            controller.bind_spa_origin(engine)
        self.assertEqual(engine.effect.call_args.args[3], {"spa": {"redirectUris": ["https://primary.pilot.example.test"]}})

    def test_waf_readback_refuses_public_frontend_tls_bypass_cross_host_and_weakened_body_checks(self):
        from shared_hub_fixture import synthetic_waf
        engine, stage = synthetic_waf()
        observer = controller.Observation(engine.client, [stage["hub"]["resourceGroupId"]])
        controller.observe_waf(engine, observer, stage)
        for mutation in ("public", "sni", "route", "body", "allow", "grant", "logs"):
            engine, stage = synthetic_waf()
            waf_id = stage["hub"]["wafId"]
            properties = engine.client.resources[ARM + waf_id + "?api-version=" + hub.WAF_API]["properties"]
            policy_id = stage["bindings"]["web"][0]["policyId"]
            policy = engine.client.resources[ARM + policy_id + "?api-version=" + hub.NETWORK_API]["properties"]
            if mutation == "public":
                properties["frontendIPConfigurations"][0]["properties"]["publicIPAddress"] = {"id": GROUP + "/providers/Microsoft.Network/publicIPAddresses/unapproved"}
            elif mutation == "sni":
                properties["backendHttpSettingsCollection"][0]["properties"]["validateSNI"] = False
            elif mutation == "route":
                properties["requestRoutingRules"][0]["properties"]["backendAddressPool"]["id"] = waf_id + "/backendAddressPools/backend-fabric"
            elif mutation == "body":
                policy["policySettings"]["requestBodyEnforcement"] = False
            elif mutation == "allow":
                policy["customRules"][0]["action"] = "Allow"
            elif mutation == "grant":
                grant_id = stage["bindings"]["web"][0]["certificateGrantId"]
                engine.client.resources[ARM + grant_id + "?api-version=2022-04-01"]["properties"]["principalId"] = GENERATION
            else:
                engine.client.collections[ARM + waf_id + "/providers/Microsoft.Insights/diagnosticSettings?api-version=2021-05-01-preview"] = [
                    {"properties": {"logs": [{"category": "ApplicationGatewayAccessLog", "enabled": True}]}}]
            with self.subTest(mutation=mutation), self.assertRaises(GateError):
                controller.observe_waf(engine, controller.Observation(engine.client, [stage["hub"]["resourceGroupId"]]), stage)

    def test_primary_parameters_and_peered_receipt_do_not_mutate_hub_or_shared_dns(self):
        from shared_hub_fixture import collect, NOW
        engine, stage, _, receipt = collect("primary", "peered")
        entry = stage["bindingInput"]["consumers"]["primary"]
        primary = {"contract": hub.PRIMARY_CONTRACT, "bindingHash": entry["bindingHash"],
                   "deploymentId": entry["deploymentId"], "expiresAtUtc": entry["expiresAtUtc"], "namePrefix": "primary",
                   "location": "francecentral", "binding": engine.manifest["binding"],
                   "names": {"workloadResourceGroup": "rg-primary-workload", "accessResourceGroup": "rg-primary-access"},
                   "network": {"vnet": entry["spoke"]["addressPrefix"], **{key: value["addressPrefix"] for key, value in entry["spoke"]["subnets"].items()}},
                   "hubBinding": {"generation": receipt["generation"], "bindingHash": receipt["hubBindingHash"],
                                  "resourceGroupId": receipt["hub"]["resourceGroupId"], "infrastructureCommit": "d" * 40},
                   "edgePolicy": entry["edgePolicy"], "containerAppsProfile": "D4", "alertEmailReceivers": [],
                   "productGrants": {}, "products": [], "actors": [], "access": {"sshPublicKey": "synthetic-public-key",
                       "linuxImage": {}, "linuxVmSize": None, "includeInteractiveClient": False, "windowsImage": {},
                       "windowsVmSize": None, "clientOperatorObjectIds": []},
                   "model": {"region": "francecentral", "enabled": False, "name": "", "version": "", "deploymentName": "", "capacity": 0}}
        hub.validate_primary_manifest(primary)
        hub.validate_receipt(receipt, primary, now=NOW)
        parameters = bootstrap.arm_parameters(primary, "edge-bind", {"hub-receipt-4": receipt})
        self.assertEqual(parameters["topology"]["value"], "shared-primary-v3")
        self.assertTrue(parameters["bindManagedEdge"]["value"])
        self.assertNotIn("vpnAudienceClientId", parameters)
        self.assertNotIn("hubBindings", parameters)
        self.assertNotIn(receipt["hub"]["resourceGroupId"], bootstrap.own_group_ids(primary))
        outputs = {"hub-receipt-4": receipt}
        with patch.object(bootstrap, "source_binding", return_value={}):
            identities = bootstrap.phase_plan(primary, "identities", outputs)
        self.assertNotIn("primary-vpn", [item["displayName"] for item in identities["directoryChanges"]["applications"]])
        with self.assertRaisesRegex(GateError, "must be connected"):
            hub.latest_receipt(outputs, required_state="connected")

    def test_pinned_schema_bytes_survive_git_clean_and_fresh_windows_linux_checkouts(self):
        source = Path(__file__).parents[1] / "contracts"
        schema_name = "shared-hub-consumer-v1.schema.json"
        original = (source / schema_name).read_bytes()
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary) / "repository"
            target = root / "infra" / "contracts"
            target.mkdir(parents=True)
            for name in (".gitattributes", schema_name):
                (target / name).write_bytes((source / name).read_bytes())

            def git(*args):
                return subprocess.run(["git", "-C", str(root), *args], check=True, capture_output=True,
                                      text=True, timeout=30).stdout.strip()

            git("init", "--quiet", "--object-format=sha1")
            git("-c", "core.autocrlf=true", "add", "infra")
            git("diff", "--cached", "--check")
            blob = git("ls-files", "--stage", "--", "infra/contracts/" + schema_name).split()[1]
            self.assertEqual(blob, git("hash-object", "--no-filters", "--", str(target / schema_name)))
            for setting, eol in (("true", "crlf"), ("false", "lf")):
                checkout = Path(temporary) / ("checkout-" + setting)
                checkout.mkdir()
                git("--work-tree=" + str(checkout), "-c", "core.autocrlf=" + setting,
                    "-c", "core.eol=" + eol, "checkout-index", "--all")
                self.assertEqual((checkout / "infra" / "contracts" / schema_name).read_bytes(), original)
            (target / schema_name).write_bytes(original.replace(b"\r\n", b" \r\n", 1))
            git("add", "infra")
            invalid = subprocess.run(["git", "-C", str(root), "diff", "--cached", "--check"],
                                     capture_output=True, text=True, timeout=30)
            self.assertNotEqual(invalid.returncode, 0)
            self.assertIn("trailing whitespace", invalid.stdout)
        self.assertIn("infra/contracts/.gitattributes", bootstrap.source_binding())

    def test_primary_import_replay_refresh_and_rebinding_keep_real_journal_semantics(self):
        from shared_hub_fixture import collect, NOW
        owner, stage, _, receipt = collect("primary", "connected")
        entry = stage["bindingInput"]["consumers"]["primary"]
        profile = {"contract": hub.PRIMARY_CONTRACT, "namePrefix": "primary", "location": "francecentral",
                   "binding": owner.manifest["binding"], "bindingHash": entry["bindingHash"],
                   "expiresAtUtc": entry["expiresAtUtc"], "names": {"workloadResourceGroup": "rg-primary-workload"},
                   "network": {"vnet": entry["spoke"]["addressPrefix"], **{key: value["addressPrefix"] for key, value in entry["spoke"]["subnets"].items()}},
                   "edgePolicy": entry["edgePolicy"], "hubBinding": {"generation": receipt["generation"],
                       "bindingHash": receipt["hubBindingHash"], "resourceGroupId": receipt["hub"]["resourceGroupId"],
                       "infrastructureCommit": "d" * 40}}
        with tempfile.TemporaryDirectory() as temporary, patch.object(hub, "utcnow", return_value=NOW + dt.timedelta(hours=1)):
            path = Path(temporary) / "receipt.json"
            args = SimpleNamespace(command="bind-hub", input=path)
            with Journal(Path(temporary) / "journal.json", profile["bindingHash"]) as journal:
                engine = bootstrap.Bootstrap(profile, journal, owner.client)
                write_json(path, receipt)
                controller.exchange_receipt(engine, args)
                controller.exchange_receipt(engine, args)
                self.assertEqual(len(journal.state["events"]), 2)
                receipt["readback"]["observedAtUtc"] = "2026-11-19T08:30:00Z"
                receipt["receiptSha256"] = digest({key: value for key, value in receipt.items() if key != "receiptSha256"})
                write_json(path, receipt, replace=True)
                controller.exchange_receipt(engine, args)
                self.assertEqual(hub.latest_receipt(engine.outputs)["receiptSha256"], receipt["receiptSha256"])
                receipt["readback"]["observedAtUtc"] = "2026-11-19T08:45:00Z"
                receipt["readback"]["compiledTemplateSha256"] = "9" * 64
                receipt["receiptSha256"] = digest({key: value for key, value in receipt.items() if key != "receiptSha256"})
                write_json(path, receipt, replace=True)
                with self.assertRaisesRegex(GateError, "Same-revision"):
                    controller.exchange_receipt(engine, args)
                self.assertEqual(len(journal.state["events"]), 4)

    def test_expired_pilot_allows_fresh_targeted_retirement_not_activation_or_resource_creation(self):
        now = dt.datetime(2026, 12, 1, 10, tzinfo=dt.timezone.utc)
        with tempfile.TemporaryDirectory() as temporary, patch.object(controller, "utcnow", return_value=now):
            root = Path(temporary)
            for state, purpose, permitted in (("offboarded", "stoppedWorkEvidenceSha256", True),
                                               ("data-bound", "private-dns", False)):
                entry = consumer("fabric")
                entry["state"] = state
                evidence = {"contract": "cohors-hub-prerequisite-evidence-1", "generation": GENERATION,
                            "consumerBindingHash": entry["bindingHash"], "sourceCommit": entry["sourceCommit"],
                            "purpose": purpose, "subjectSha256": controller.evidence_subject(entry), "passed": True,
                            "observedAtUtc": "2026-12-01T09:00:00Z", "expiresAtUtc": "2026-12-01T11:00:00Z"}
                path = root / (state + ".json")
                write_json(path, evidence)
                expected = file_hash(path)
                approval = {"hubEvidenceFiles": {expected: path.name}}
                if permitted:
                    controller.check_evidence(root, approval, manifest(), entry, [(purpose, expected)])
                else:
                    with self.assertRaisesRegex(GateError, "stale"):
                        controller.check_evidence(root, approval, manifest(), entry, [(purpose, expected)])
        create = {"status": "Succeeded", "changes": [{"resourceId": GROUP, "changeType": "Create"}]}
        with self.assertRaisesRegex(GateError, "cannot create"):
            bootstrap.validate_what_if(create, manifest(), removal_only=True)
        bootstrap.validate_what_if({"status": "Succeeded", "changes": []}, manifest(), removal_only=True)
        with self.assertRaisesRegex(GateError, "partially completed"):
            controller.require_retirement(SimpleNamespace(outputs={"arm-hub-bindings-000002": {}}))

    def test_cached_observation_cannot_skip_a_later_ownership_check(self):
        from shared_hub_fixture import synthetic_stage
        owner, stage = synthetic_stage()
        identifier = stage["hub"]["vnetId"]
        observer = controller.Observation(owner.client, [stage["hub"]["resourceGroupId"]])
        observer.get(identifier)
        with self.assertRaisesRegex(GateError, "not owned"):
            observer.get(identifier, owner="9" * 64)
        with self.assertRaisesRegex(GateError, "provider versions"):
            observer.get(identifier, "2024-01-01")

    def test_targeted_release_is_journaled_if_match_bound_and_not_repeated_after_resume(self):
        from shared_hub_fixture import collect
        owner, stage, observer, _ = collect("fabric", "connected")
        stage["metadataReadback"]["resources"] = observer.bindings()
        target = stage["hub"]["vnetId"] + "/virtualNetworkPeerings/to-fabric"
        url = ARM + target + "?api-version=" + hub.NETWORK_API
        # Removing the consumer half changes the surviving hub peer's state, not its ownership.
        owner.client.resources[url]["properties"]["peeringState"] = "Disconnected"
        real_read = owner.client.request
        deletes = []

        def request(method, address, body=None, *, allowed=(200,), headers=None):
            if method == "DELETE":
                self.assertEqual(address, url)
                self.assertEqual(headers, {"If-Match": 'W/"synthetic-fixture-1"'})
                deletes.append(address)
                del owner.client.resources[address]
                return {}, {}, 204
            return real_read(method, address, body, allowed=allowed, headers=headers)

        with tempfile.TemporaryDirectory() as temporary, Journal(Path(temporary) / "journal.json", owner.manifest["bindingHash"]) as journal:
            journal.begin("phase-hub-bindings-000004", "LOCAL-CHECKPOINT", "fixture", {})
            journal.complete("phase-hub-bindings-000004", stage)
            engine = bootstrap.Bootstrap(owner.manifest, journal, owner.client)
            plan = {"phase": "hub-bindings", "operationPhase": "hub-bindings-000005", "bindingInput": stage["bindingInput"]}
            with patch.object(owner.client, "request", side_effect=request):
                controller.delete_reference(engine, plan, target)
                controller.delete_reference(engine, plan, target)
            self.assertEqual(len(deletes), 1)
            self.assertIn(ARM + stage["hub"]["firewallId"] + "?api-version=" + hub.NETWORK_API, owner.client.resources)
            with self.assertRaisesRegex(GateError, "outside"):
                controller.delete_reference(engine, plan, stage["bindingInput"]["consumers"]["fabric"]["spoke"]["vnetId"])

    def test_waf_offboarding_accepts_own_detach_and_resumes_without_repeating_writes(self):
        for keep_primary in (True, False):
            with self.subTest(keep_primary=keep_primary), waf_offboarding(keep_primary) as case:
                primary_before = {url: copy.deepcopy(value) for url, value in case.owner.client.resources.items()
                                  if url.endswith("/primary?api-version=" + hub.NETWORK_API)}
                with Journal(case.journal_path, case.owner.manifest["bindingHash"]) as journal:
                    engine = bootstrap.Bootstrap(case.owner.manifest, journal, case.owner.client)
                    result = case.apply(engine)
                # Resume after the approved ARM detach, using the original durable journal.
                for _ in range(2):
                    with Journal(case.journal_path, case.owner.manifest["bindingHash"]) as journal:
                        engine = bootstrap.Bootstrap(case.owner.manifest, journal, case.owner.client)
                        result = case.apply(engine)
                        controller.complete_hub_phase(engine, case.plan, result, "e" * 64)
                self.assertEqual(len(case.puts), 1)
                self.assertEqual(len(case.deletes), 2 if keep_primary else 3)
                self.assertEqual(sum(url == case.policy_url for url, _ in case.deletes), 1)
                self.assertIn((case.policy_url, {"If-Match": 'W/"synthetic-after-approved-detach"'}), case.deletes)
                self.assertIn(case.policy_url, case.absent)
                self.assertEqual(case.gateway_url in case.owner.client.resources, keep_primary)
                for url, value in primary_before.items():
                    self.assertEqual(case.owner.client.resources[url], value)
                if keep_primary:
                    self.assertEqual([item["name"] for item in case.owner.client.resources[case.gateway_url]["properties"]["httpListeners"]],
                                     ["https-primary"])
                else:
                    self.assertIn(case.gateway_url, case.absent)

    def test_waf_offboarding_refuses_drift_and_remaining_or_foreign_associations(self):
        for mutation in ("before-detach", "identity", "ownership", "settings", "managed", "custom", "gateway",
                         "listener", "own-listener", "path", "containers", "etag", "remaining-primary", "gateway-output", "plan"):
            with self.subTest(mutation=mutation), waf_offboarding(True) as case:
                with Journal(case.journal_path, case.owner.manifest["bindingHash"]) as journal:
                    engine = bootstrap.Bootstrap(case.owner.manifest, journal, case.owner.client)
                    actual = case.owner.client.resources[case.policy_url]
                    if mutation == "before-detach":
                        actual["properties"]["policySettings"]["mode"] = "Detection"
                        with self.assertRaisesRegex(GateError, "drifted before"):
                            case.apply(engine)
                        self.assertFalse(case.puts)
                        continue
                    result = case.apply(engine)
                    if mutation == "identity":
                        actual["id"] += "-foreign"
                    elif mutation == "ownership":
                        actual["tags"]["cohorsBinding"] = "9" * 64
                    elif mutation == "settings":
                        actual["properties"]["policySettings"]["mode"] = "Detection"
                    elif mutation == "managed":
                        actual["properties"]["managedRules"]["managedRuleSets"][0]["ruleSetVersion"] = "3.1"
                    elif mutation == "custom":
                        actual["properties"]["customRules"][0]["action"] = "Allow"
                    elif mutation in ("gateway", "listener", "path"):
                        key = {"gateway": "applicationGateways", "listener": "httpListeners", "path": "pathBasedRules"}[mutation]
                        actual["properties"][key] = [{"id": case.previous["hub"]["wafId"] + "/unapproved-" + mutation}]
                    elif mutation == "own-listener":
                        actual["properties"]["httpListeners"] = [{"id": case.previous["hub"]["wafId"] + "/httpListeners/https-fabric"}]
                    elif mutation == "containers":
                        actual["properties"]["applicationGatewayForContainers"] = [{"id": GROUP + "/providers/Microsoft.ServiceNetworking/trafficControllers/unapproved"}]
                    elif mutation == "etag":
                        actual.pop("etag")
                    elif mutation == "remaining-primary":
                        case.owner.client.resources[case.gateway_url]["properties"]["httpListeners"][0]["properties"]["hostNames"] = ["foreign.example.test"]
                    elif mutation == "gateway-output":
                        result["hub"]["wafId"] = None
                    else:
                        case.plan["sharedResourceImpact"] += " changed"
                    with self.assertRaises(GateError):
                        controller.complete_hub_phase(engine, case.plan, result, "e" * 64)
                    self.assertFalse(case.delete_attempts)
                    self.assertIn(case.policy_url, case.owner.client.resources)
                    self.assertTrue(all("unapproved" not in url and "foreign" not in url for _, url in case.owner.client.calls))

    def test_waf_offboarding_waits_for_actual_404_without_repeating_a_completed_delete(self):
        for target in ("gateway", "policy"):
            with self.subTest(target=target), waf_offboarding(target == "policy") as case:
                url = case.gateway_url if target == "gateway" else case.policy_url
                case.hold_absence.add(url)
                for _ in range(2):
                    with Journal(case.journal_path, case.owner.manifest["bindingHash"]) as journal:
                        engine = bootstrap.Bootstrap(case.owner.manifest, journal, case.owner.client)
                        result = case.apply(engine)
                        with self.assertRaisesRegex(GateError, "not yet observed absent"):
                            controller.complete_hub_phase(engine, case.plan, result, "e" * 64)
                    self.assertEqual(case.delete_attempts.count(url), 1)
                # Synthetic eventual provider disappearance is not an invented second owner request.
                del case.owner.client.resources[url]
                with Journal(case.journal_path, case.owner.manifest["bindingHash"]) as journal:
                    engine = bootstrap.Bootstrap(case.owner.manifest, journal, case.owner.client)
                    controller.complete_hub_phase(engine, case.plan, case.apply(engine), "e" * 64)
                self.assertEqual(case.delete_attempts.count(url), 1)
                self.assertIn(url, case.absent)

    def test_waf_offboarding_refuses_a_missing_original_checkpoint_or_uncertain_delete_replay(self):
        for missing_original in (True, False):
            with self.subTest(missing_original=missing_original), waf_offboarding(True) as case:
                with Journal(case.journal_path, case.owner.manifest["bindingHash"]) as journal:
                    engine = bootstrap.Bootstrap(case.owner.manifest, journal, case.owner.client)
                    if missing_original:
                        # Model the pre-fix journal: the real ARM effect existed, but no pre-detach witness did.
                        with patch.object(controller, "capture_waf_policy_releases"):
                            case.apply(engine)
                    else:
                        result = case.apply(engine)
                        case.race_etag = True
                        with self.assertRaisesRegex(GateError, "412"):
                            controller.complete_hub_phase(engine, case.plan, result, "e" * 64)
                with Journal(case.journal_path, case.owner.manifest["bindingHash"]) as journal:
                    engine = bootstrap.Bootstrap(case.owner.manifest, journal, case.owner.client)
                    if missing_original:
                        with self.assertRaisesRegex(GateError, "original pre-detach"):
                            case.apply(engine)
                        self.assertFalse(case.delete_attempts)
                    else:
                        with self.assertRaisesRegex(GateError, "unresolved intent"):
                            controller.complete_hub_phase(engine, case.plan, case.apply(engine), "e" * 64)
                        self.assertEqual(case.delete_attempts, [case.policy_url])
                self.assertEqual(len(case.puts), 1)
                self.assertFalse(case.deletes)

    def test_completed_arm_resume_reads_exact_parameters_without_a_second_write(self):
        from shared_hub_fixture import synthetic_stage
        owner, stage = synthetic_stage("fabric", "connected")
        owner.outputs["phase-edge-bind"] = stage
        plan = {"phase": "hub-bindings", "operationPhase": "hub-bindings-000005", "bindingInput": stage["bindingInput"]}
        identifier = "/subscriptions/" + SUBSCRIPTION + "/providers/Microsoft.Resources/deployments/fixture-revision"
        owner.outputs["arm-hub-bindings-000005"] = {"id": identifier}
        url = ARM + identifier + "?api-version=2025-04-01"
        parameters = bootstrap.arm_parameters(owner.manifest, "hub-bindings", owner.outputs, binding_input=stage["bindingInput"])
        owner.client.resources[url] = {"properties": {"provisioningState": "Succeeded", "parameters": parameters,
                                                       "outputs": {"hub": {"value": stage["hub"]}}}}
        with tempfile.TemporaryDirectory() as temporary:
            preview = Path(temporary) / "preview.json"
            write_json(preview, {"planHash": digest(plan), "result": {"status": "Succeeded", "changes": []}})
            result = bootstrap.arm_apply(owner, "hub-bindings", plan, {"whatIfSha256": file_hash(preview)}, preview)
            self.assertEqual(result["hub"], stage["hub"])
            owner.client.resources[url]["properties"]["parameters"]["profile"]["value"]["bindingHash"] = "9" * 64
            with self.assertRaisesRegex(GateError, "exact approved revision"):
                bootstrap.arm_apply(owner, "hub-bindings", plan, {"whatIfSha256": file_hash(preview)}, preview)
            self.assertTrue(all(method == "GET" for method, _ in owner.client.calls))

    def test_arm_name_bindings_come_from_actual_hub_ids_and_reviewed_secret_uri(self):
        from shared_hub_fixture import synthetic_waf
        owner, stage = synthetic_waf()
        owner.outputs["phase-edge-bind"] = stage
        parameters = controller.parameters(owner.manifest, "hub-bindings", owner.outputs, stage["bindingInput"])
        self.assertEqual(parameters["hubResourceNames"]["value"], {
            "vnet": "vnet-testhub-hub", "certificateVault": "kv-synthetic-hub", "certificates": {"primary": "frontend"}})
        stage["hub"]["vnetId"] = stage["bindingInput"]["consumers"]["primary"]["spoke"]["vnetId"]
        with self.assertRaises(GateError):
            controller.parameters(owner.manifest, "hub-bindings", owner.outputs, stage["bindingInput"])


if __name__ == "__main__":
    unittest.main()
