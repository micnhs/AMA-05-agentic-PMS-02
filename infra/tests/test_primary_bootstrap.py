import copy
import datetime as dt
import importlib.util
from pathlib import Path
import sys
import unittest


sys.path.insert(0, str(Path(__file__).parents[1] / "scripts"))
import primary_bootstrap as bootstrap
from primary_control import GateError, digest


class PrimaryBootstrapTests(unittest.TestCase):
    def setUp(self):
        self.now = dt.datetime(2026, 11, 18, 12, tzinfo=dt.timezone.utc)
        self.manifest = {
            "bindingHash": "b" * 64, "deploymentId": "11111111-2222-4333-8444-555555555555",
            "namePrefix": "cohorstest", "expiresAtUtc": "2026-11-25T12:00:00Z",
            "binding": {"principalObjectId": "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee",
                        "subscriptionId": "00000000-1111-4222-8333-444444444444"},
            "names": {"workloadResourceGroup": "rg-cohorstest-workload", "accessResourceGroup": "rg-cohorstest-access"},
        }
        self.plan = {"phase": "identities", "bindingHash": self.manifest["bindingHash"]}
        self.approval = {
            "contract": "cohors-primary-phase-approval-1", "bindingHash": self.manifest["bindingHash"],
            "planHash": digest(self.plan), "phase": "identities",
            "reviewerObjectId": self.manifest["binding"]["principalObjectId"],
            "reviewedAtUtc": "2026-11-18T11:00:00Z", "expiresAtUtc": "2026-11-18T13:00:00Z",
            "acknowledgedEffects": bootstrap.EFFECTS["identities"], "riskAcknowledged": True,
            "policyReviewSha256": "1" * 64, "readinessEvidenceSha256": "2" * 64, "costReviewSha256": "3" * 64,
        }

    def test_exact_privileged_phase_approval_not_architecture_flag(self):
        bootstrap.approval_check(self.approval, self.plan, self.manifest, self.now)
        for key, value in (("bindingHash", "c" * 64), ("planHash", "d" * 64),
                           ("acknowledgedEffects", []), ("expiresAtUtc", "2026-11-18T11:00:00Z"),
                           ("reviewerObjectId", "another-user"), ("riskAcknowledged", False)):
            changed = {**self.approval, key: value}
            with self.subTest(key=key), self.assertRaises(GateError):
                bootstrap.approval_check(changed, self.plan, self.manifest, self.now)

    def test_what_if_rejects_foreign_delete_and_unknown_results(self):
        own = bootstrap.own_group_ids(self.manifest)[0]
        result = {"status": "Succeeded", "changes": [{"resourceId": own, "changeType": "Create"}]}
        bootstrap.validate_what_if(result, self.manifest)
        for change in (
            {"resourceId": own + "-foreign", "changeType": "Create"},
            {"resourceId": own, "changeType": "Delete"},
            {"resourceId": own, "changeType": "Ignore"},
            {"resourceId": own, "changeType": "Modify", "before": {"tags": {"cohorsBinding": "foreign"}}},
        ):
            with self.subTest(change=change), self.assertRaises(GateError):
                bootstrap.validate_what_if({"status": "Succeeded", "changes": [change]}, self.manifest)

    def test_own_app_only_roles_and_human_roles_are_not_interchangeable(self):
        api = bootstrap.app_body(self.manifest, "api")
        for role in api["appRoles"]:
            self.assertEqual(role["allowedMemberTypes"], ["Application"] if role["value"] == "Workflow" else ["User"])
        agent = bootstrap.app_body(self.manifest, "agents")
        self.assertEqual([role["value"] for role in agent["appRoles"]], ["Agent"])
        self.assertEqual(agent["api"]["oauth2PermissionScopes"], [])
        self.assertEqual(api["api"]["requestedAccessTokenVersion"], 2)

    def test_prerequisite_ids_cannot_be_substituted_for_completed_phase(self):
        with self.assertRaisesRegex(GateError, "must complete first"):
            bootstrap.phase_plan(self.manifest, "foundation", {"externalResourceIds": {"sqlServer": "existing"}})

    def test_metadata_cannot_waive_the_unbuilt_operations_contract(self):
        self.plan["phase"] = "runtime"
        self.manifest["releaseBundle"] = {"applicationCommit": "a" * 40}
        self.manifest["operationsSqlContract"] = {"applicationCommit": "a" * 40, "accepted": True}
        self.approval.update(phase="runtime", planHash=digest(self.plan),
                             acknowledgedEffects=bootstrap.EFFECTS["runtime"], whatIfSha256="4" * 64)
        with self.assertRaisesRegex(GateError, "not yet hosted-qualified"):
            bootstrap.approval_check(self.approval, self.plan, self.manifest, self.now)

    def test_consumption_is_not_mislabelled_as_strict_dns_dedicated(self):
        self.plan["phase"] = "network"
        self.manifest["containerAppsProfile"] = "Consumption"
        self.approval.update(phase="network", planHash=digest(self.plan),
                             acknowledgedEffects=bootstrap.EFFECTS["network"], whatIfSha256="4" * 64)
        with self.assertRaisesRegex(GateError, "Consumption requires AzurePlatformDNS"):
            bootstrap.approval_check(self.approval, self.plan, self.manifest, self.now)
        self.manifest["containerAppsProfile"] = "D4"
        bootstrap.approval_check(self.approval, self.plan, self.manifest, self.now)


if __name__ == "__main__":
    unittest.main()
