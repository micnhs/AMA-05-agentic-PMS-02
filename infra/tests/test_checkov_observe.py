import copy
from contextlib import redirect_stdout
from enum import Enum
import io
import json
from pathlib import Path
import subprocess
import sys
import tempfile
from types import SimpleNamespace
import unittest

sys.path.insert(0, str(Path(__file__).parents[1] / "scripts"))
from checkov_observe import Observation, assess
from checkov_templates import export


class Result(str, Enum):
    UNKNOWN = "UNKNOWN"


class CheckovObservationTests(unittest.TestCase):
    def test_results_and_exceptions_are_never_changed_or_suppressed(self):
        observation = Observation()
        check = SimpleNamespace(id="CKV_TEST_1")
        for outcome in ("PASSED", "FAILED", "SKIPPED", Result.UNKNOWN, None):
            expected = {"result": outcome, "evaluated_keys": ["properties/x"]}
            wrapped = observation.wrap(lambda *args: expected)
            self.assertIs(wrapped(check, "scan.json", {}, "test", "resource", None), expected)
        error = TypeError("synthetic original check failure")

        def fail(*args):
            raise error

        with self.assertRaises(TypeError) as caught:
            observation.wrap(fail)(check, "scan.json", {}, "test", "resource", None)
        self.assertIs(caught.exception, error)
        report = observation.report()
        self.assertEqual(report["executed"], 6)
        self.assertEqual(report["counts"]["UNKNOWN"], 1)
        self.assertEqual(report["counts"]["EXECUTION_ERROR"], 1)
        self.assertFalse(report["completeWithoutUnknownSkippedOrErrors"])

    def test_no_observed_checks_is_not_a_pass(self):
        observation = Observation()
        self.assertFalse(observation.report()["completeWithoutUnknownSkippedOrErrors"])
        observation.wrap(lambda *args: {"result": "PASSED"})(SimpleNamespace(id="CKV_TEST_1"), "scan", {}, "r", "t", None)
        self.assertTrue(observation.report()["completeWithoutUnknownSkippedOrErrors"])

    def scoped_case(self, vault_result="PASSED", legacy_result="PASSED", network_results=("FAILED", "UNKNOWN")):
        if not hasattr(self, "scoped_source"):
            resources = []
            for name, topology in (("legacy", "separate-primary-v2"), ("hub", "shared-hub-v1"), ("spoke", "shared-primary-v3")):
                children = [{"type": "Microsoft.Network/virtualNetworks", "name": name + "-network"}]
                if name == "hub":
                    children.append({"type": "Microsoft.KeyVault/vaults", "name": "synthetic-vault"})
                resources.append({"type": "Microsoft.Resources/deployments", "name": name,
                                  "condition": f"[equals(parameters('topology'), '{topology}')]",
                                  "properties": {"template": {"resources": children}}})
            self.scoped_source = json.dumps({"resources": resources}).encode()
            with tempfile.TemporaryDirectory() as temporary, redirect_stdout(io.StringIO()):
                path = Path(temporary)
                (path / "source.json").write_bytes(self.scoped_source)
                export(path / "source.json", path / "scan")
                self.scoped_manifest = json.loads((path / "scan.manifest.json").read_bytes())
        manifest = copy.deepcopy(self.scoped_manifest)
        observation = Observation()
        for index, (view, kind, result) in enumerate((
            (2, "Microsoft.Network/virtualNetworks", network_results[0]),
            (3, "Microsoft.Network/virtualNetworks", network_results[1]),
            (2, "Microsoft.KeyVault/vaults", vault_result),
            (1, "Microsoft.Network/virtualNetworks", legacy_result),
        )):
            observation.wrap(lambda *args, outcome=result: {"result": outcome})(
                SimpleNamespace(id=f"CKV_TEST_{index}"), f"/compiled/SCAN-ONLY-{view:03}.json",
                {}, f"synthetic-{index}", kind, None)
        wire = json.loads(json.dumps(observation.report()))
        summary = {field: wire["counts"].get(result, 0)
                   for field, result in (("passed", "PASSED"), ("failed", "FAILED"), ("skipped", "SKIPPED"))}
        summary.update(parsing_errors=0, resource_count=4)
        review = {"scope": "shared-private-network", "disposition": "deferred"}
        return {"summary": summary}, wire, manifest, self.scoped_source, review

    def test_only_shared_network_failures_are_deferred_without_relabeling_original_results(self):
        values = self.scoped_case()
        before = copy.deepcopy(values)
        result = assess("primary-network", *values, 1, False)
        self.assertTrue(result["requiredChecksPassed"])
        self.assertFalse(result["passed"])
        self.assertEqual(result["deferred"]["counts"], {"FAILED": 1, "UNKNOWN": 1})
        self.assertEqual(result["required"]["counts"], {"PASSED": 2})
        self.assertEqual(values, before)
        values[4]["disposition"] = "required"
        self.assertFalse(assess("primary-network", *values, 1, False)["requiredChecksPassed"])

    def test_actual_complete_success_is_preserved_with_or_without_the_network_deferral(self):
        for disposition in ("required", "deferred"):
            values = self.scoped_case(network_results=("PASSED", "PASSED"))
            values[4]["disposition"] = disposition
            result = assess("primary-network", *values, 0, False)
            self.assertTrue(result["passed"])
            self.assertTrue(result["requiredChecksPassed"])
            self.assertEqual(result["integrityErrors"], [])

    def test_non_network_and_unmapped_negative_or_unassessed_results_stay_blocking(self):
        for result in ("FAILED", "UNKNOWN", "SKIPPED", "EXECUTION_ERROR", None):
            with self.subTest(non_network=result):
                assessment = assess("primary-network", *self.scoped_case(vault_result=result), 1, False)
                self.assertFalse(assessment["requiredChecksPassed"])
        self.assertFalse(assess("primary-network", *self.scoped_case(legacy_result="FAILED"), 1, False)["requiredChecksPassed"])
        for root in ("main", "primary"):
            self.assertFalse(assess(root, *self.scoped_case(), 1, False)["requiredChecksPassed"])
        for field in ("unknown-file", "unmapped-type", "non-network-provider", "other-branch", "mixed-root"):
            with self.subTest(binding=field):
                report, observation, manifest, source, review = self.scoped_case()
                if field == "unknown-file":
                    observation["records"][0]["file"] = "unbound.json"
                elif field == "unmapped-type":
                    observation["records"][0]["entityType"] = "Microsoft.Network/routeTables"
                elif field == "non-network-provider":
                    observation["records"][0]["entityType"] = "Microsoft.Authorization/roleAssignments"
                elif field == "other-branch":
                    observation["records"][0]["file"] = "/compiled/SCAN-ONLY-001.json"
                else:
                    observation["records"][0]["file"] = "/compiled/SCAN-ONLY-000.json"
                self.assertFalse(assess("primary-network", report, observation, manifest, source, review, 1, False)["requiredChecksPassed"])

    def test_unreconciled_totals_tool_failures_or_historic_totals_cannot_be_waived(self):
        for field in ("report-total", "execution-total", "boolean-count", "empty-coverage", "parsing-error", "bad-exit", "diagnostics"):
            with self.subTest(failure=field):
                report, observation, manifest, source, review = self.scoped_case()
                if field == "report-total":
                    report["summary"]["failed"] += 1
                elif field == "execution-total":
                    observation["executed"] += 1
                elif field == "boolean-count":
                    observation["counts"]["FAILED"] = True
                elif field == "empty-coverage":
                    report["summary"]["resource_count"] = 0
                elif field == "parsing-error":
                    report["summary"]["parsing_errors"] = 1
                result = assess("primary-network", report, observation, manifest, source, review,
                                2 if field == "bad-exit" else 1, field == "diagnostics")
                self.assertFalse(result["requiredChecksPassed"])
                self.assertTrue(result["integrityErrors"])
        report, observation, manifest, source, review = self.scoped_case()
        del observation["records"]
        with self.assertRaisesRegex(ValueError, "historic totals"):
            assess("primary-network", report, observation, manifest, source, review, 1, False)
        report, observation, manifest, source, review = self.scoped_case()
        manifest["templates"].append(copy.deepcopy(manifest["templates"][0]))
        with self.assertRaisesRegex(ValueError, "Ambiguous"):
            assess("primary-network", report, observation, manifest, source, review, 1, False)
        review["scope"] = "all-infrastructure"
        with self.assertRaisesRegex(ValueError, "exact shared"):
            assess("primary-network", report, observation, manifest, source, review, 1, False)
        report, observation, manifest, source, review = self.scoped_case()
        with self.assertRaisesRegex(ValueError, "original compiled bytes"):
            assess("primary-network", report, observation, manifest, source + b"\n", review, 1, False)
        manifest["templates"][2]["resources"][0]["sourceSha256"] = "0" * 64
        with self.assertRaisesRegex(ValueError, "original resource bindings"):
            assess("primary-network", report, observation, manifest, source, review, 1, False)

    def test_existing_exporter_and_assessment_cli_bind_the_real_branch_path_and_review(self):
        leaf = {"resources": {"network": {"type": "Microsoft.Network/virtualNetworks", "name": "synthetic-network"}}}
        nested = {"resources": {"network": {"type": "Microsoft.Resources/deployments", "name": "synthetic-inner",
                                           "properties": {"template": leaf}}}}
        source = {"resources": {"hub": {"type": "Microsoft.Resources/deployments", "name": "synthetic-hub",
                                       "condition": "[equals(parameters('topology'), 'shared-hub-v1')]",
                                       "properties": {"template": nested}}}}
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            compiled = root / "primary-network.json"
            compiled.write_text(json.dumps(source), encoding="utf-8")
            export(compiled, root / "scan")
            manifest = root / "scan.manifest.json"
            view = json.loads(manifest.read_bytes())["templates"][1]
            self.assertEqual(view["templatePath"], ["hub"])
            observation = Observation()
            observation.wrap(lambda *args: {"result": "FAILED"})(
                SimpleNamespace(id="CKV_TEST_NETWORK"), "/compiled/" + view["file"], {}, "synthetic-network",
                "Microsoft.Network/virtualNetworks", None)
            (root / "observation.json").write_text(json.dumps(observation.report()), encoding="utf-8")
            report = {"summary": {"passed": 0, "failed": 1, "skipped": 0, "parsing_errors": 0, "resource_count": 1}}
            (root / "report.json").write_text(json.dumps(report), encoding="utf-8")
            result = subprocess.run(
                [sys.executable, "-B", str(Path(__file__).parents[1] / "scripts" / "checkov_observe.py"),
                 "--assess", "primary-network", str(root / "report.json"), str(root / "observation.json"),
                 str(manifest), "1", "false"], capture_output=True, text=True, check=True)
            assessment = json.loads(result.stdout)
            self.assertFalse(assessment["passed"])
            self.assertTrue(assessment["requiredChecksPassed"])
            self.assertEqual(assessment["review"]["disposition"], "deferred")
            self.assertTrue(assessment["review"]["notASecurityPass"])


if __name__ == "__main__":
    unittest.main()
