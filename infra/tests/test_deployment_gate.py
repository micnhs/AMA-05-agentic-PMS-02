import copy
import sys
import unittest
from datetime import datetime, timedelta, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts"))
from deployment_gate import REQUIRED_GATES, SQL_THREAT_GATES, validate


class DeploymentGateTests(unittest.TestCase):
    def setUp(self):
        # Synthetic, offline identifiers; none represents discovered Azure resources.
        self.now = datetime(2026, 1, 1, tzinfo=timezone.utc)
        self.context = {
            "sourceSha": "ab" * 20, "reviewId": "synthetic-unit-review",
            "tenantId": "11111111-1111-4111-8111-111111111111",
            "subscriptionId": "22222222-2222-4222-8222-222222222222", "resourceGroup": "rg-test",
        }
        scope = f"/subscriptions/{self.context['subscriptionId']}/resourceGroups/rg-platform/providers/"
        self.values = {
            "location": "westeurope",
            "sqlAdminGroupObjectId": "33333333-3333-4333-8333-333333333333",
            "sqlAdminGroupName": "test-admins",
            "egressRouteTableResourceId": scope + "Microsoft.Network/routeTables/rt-test",
            "actionGroupResourceId": scope + "Microsoft.Insights/actionGroups/ag-test",
        }
        self.approval = {
            "schemaVersion": 1, "reviewId": self.context["reviewId"], "parametersSha256": "cd" * 32,
            "expiresUtc": (self.now + timedelta(days=1)).isoformat(),
            "tenantId": self.context["tenantId"], "subscriptionId": self.context["subscriptionId"],
            "resourceGroup": self.context["resourceGroup"], "location": "westeurope",
            "gates": {key: {"approved": True, "evidence": "offline-test-evidence"} for key in REQUIRED_GATES},
        }

    def check(self):
        return validate({"parameters": {key: {"value": value} for key, value in self.values.items()}},
                        self.approval, self.context, "cd" * 32, self.now)

    def enable_runtime(self):
        api = "44444444-4444-4444-8444-444444444444"
        agents = "55555555-5555-4555-8555-555555555555"
        self.values.update({
            "deployRuntime": True, "deployModel": True, "enableWatchdogAlert": True,
            "apiImageDigest": "ab" * 32, "agentsImageDigest": "cd" * 32,
            "apiAudience": api, "agentsAudience": agents,
            "spaClientId": "66666666-6666-4666-8666-666666666666",
            "workflowApiScope": f"api://{api}/.default", "agentsApiScope": f"api://{agents}/.default",
            "spaDelegatedScope": f"api://{api}/access_as_user", "agentProducts": ["ortho"],
            "modelName": "synthetic-model", "modelVersion": "test-version", "modelCapacity": 1,
            "foundryBoundaryReview": {
                "reference": "offline-test", "configurationHash": "ef" * 32,
                "deploymentSku": "Standard", "deploymentRegion": "westeurope",
                "deploymentGeography": "offline-test-geography", "processingCountries": ["NL"],
            },
        })
        for key in ("modelVersionCapacityGeography", "runtimeArtifactsAndBootstrap", "watchdogQueryReadiness"):
            self.approval["gates"][key] = {"approved": True, "evidence": "offline-test-evidence"}

    def test_reviewed_foundation_and_runtime_inputs(self):
        self.check()
        self.enable_runtime()
        self.check()

    def test_every_platform_gate_is_required(self):
        for key in REQUIRED_GATES:
            with self.subTest(gate=key):
                self.approval["gates"][key]["approved"] = False
                with self.assertRaisesRegex(ValueError, key):
                    self.check()
                self.approval["gates"][key]["approved"] = True

    def test_changed_parameters_or_expired_review_are_rejected(self):
        self.approval["parametersSha256"] = "ef" * 32
        with self.assertRaisesRegex(ValueError, "digest"):
            self.check()
        self.approval["parametersSha256"] = "cd" * 32
        self.approval["expiresUtc"] = (self.now - timedelta(seconds=1)).isoformat()
        with self.assertRaisesRegex(ValueError, "expired"):
            self.check()

    def test_context_switches_and_placeholder_ids_are_rejected(self):
        self.approval["subscriptionId"] = self.context["tenantId"]
        with self.assertRaisesRegex(ValueError, "subscriptionId"):
            self.check()
        self.approval["subscriptionId"] = self.context["subscriptionId"]
        self.values["sqlAdminGroupObjectId"] = "00000000-0000-0000-0000-000000000000"
        with self.assertRaisesRegex(ValueError, "placeholder"):
            self.check()

    def test_overlapping_or_public_networks_are_rejected(self):
        self.values["operationsSubnetPrefix"] = "10.42.2.0/26"
        with self.assertRaisesRegex(ValueError, "overlap"):
            self.check()
        self.values.pop("operationsSubnetPrefix")
        self.values["vnetPrefix"] = "8.8.0.0/16"
        with self.assertRaisesRegex(ValueError, "private IPv4"):
            self.check()

    def test_model_boundary_must_be_explicit_standard_and_eu(self):
        self.enable_runtime()
        baseline = copy.deepcopy(self.values["foundryBoundaryReview"])
        for mutation in ({"deploymentSku": "GlobalStandard"}, {"deploymentSku": "DataZoneStandard"},
                         {"deploymentSku": "DeveloperTier"}, {"processingCountries": ["NO"]},
                         {"processingCountries": []}, {"configurationHash": ""}):
            with self.subTest(mutation=mutation):
                self.values["foundryBoundaryReview"] = baseline | mutation
                with self.assertRaises(ValueError):
                    self.check()

    def test_runtime_cannot_start_without_artifacts_or_watchdog(self):
        self.enable_runtime()
        for key, bad in (("apiImageDigest", "latest"), ("enableWatchdogAlert", False),
                         ("agentProducts", []), ("deployRuntime", "false")):
            with self.subTest(parameter=key):
                original = self.values[key]
                self.values[key] = bad
                with self.assertRaises(ValueError):
                    self.check()
                self.values[key] = original

    def test_alert_requires_query_readiness_even_without_runtime(self):
        self.check()
        for runtime in (False, True):
            with self.subTest(runtime=runtime):
                if runtime:
                    self.enable_runtime()
                self.values["enableWatchdogAlert"] = True
                self.approval["gates"].pop("watchdogQueryReadiness", None)
                with self.assertRaisesRegex(ValueError, "watchdogQueryReadiness"):
                    self.check()
                for gate in ({"approved": False, "evidence": "offline-test"},
                             {"approved": True, "evidence": ""}):
                    self.approval["gates"]["watchdogQueryReadiness"] = gate
                    with self.assertRaisesRegex(ValueError, "watchdogQueryReadiness"):
                        self.check()
                self.approval["gates"]["watchdogQueryReadiness"] = {
                    "approved": True, "evidence": "offline-test-only",
                }
                self.check()

    def test_no_shared_audience_or_cross_api_scope(self):
        self.enable_runtime()
        self.values["agentsAudience"] = f"api://{self.values['apiAudience']}"
        with self.assertRaisesRegex(ValueError, "separate audiences"):
            self.check()
        self.values["agentsAudience"] = "55555555-5555-4555-8555-555555555555"
        self.values["workflowApiScope"] = self.values["agentsApiScope"]
        with self.assertRaisesRegex(ValueError, "different API"):
            self.check()

    def test_no_unreviewed_digest_writer_or_telemetry_drift(self):
        self.values["enableDigestPublisherGrant"] = True
        with self.assertRaisesRegex(ValueError, "independentDigestPublisher"):
            self.check()
        self.values.pop("enableDigestPublisherGrant")
        self.values["operationsTelemetryRole"] = "anything-else"
        with self.assertRaisesRegex(ValueError, "Operations emitter"):
            self.check()

    def test_threat_protection_activation_requires_each_specialized_review_and_recipients(self):
        self.values["deploySqlThreatProtection"] = True
        with self.assertRaisesRegex(ValueError, "recipients"):
            self.check()
        self.values["sqlSecurityNotificationEmails"] = ["security@example.invalid"]
        for key in SQL_THREAT_GATES:
            self.approval["gates"][key] = {"approved": True, "evidence": "offline-test-only"}
        self.check()
        for key in SQL_THREAT_GATES:
            with self.subTest(gate=key):
                self.approval["gates"][key]["approved"] = False
                with self.assertRaisesRegex(ValueError, key):
                    self.check()
                self.approval["gates"][key]["approved"] = True


if __name__ == "__main__":
    unittest.main()
