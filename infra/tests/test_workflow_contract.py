import ast
import datetime as dt
import re
import unittest
from pathlib import Path

import yaml


ROOT = Path(__file__).resolve().parents[2]


class WorkflowContractTests(unittest.TestCase):
    def diagnostic_helpers(self):
        workflow = yaml.safe_load((ROOT / ".github/workflows/cohors-recovery-copy-diagnostic.yml").read_text(encoding="utf-8"))
        body = next(step["run"] for step in workflow["jobs"]["diagnostic"]["steps"] if "run" in step)
        code = body.split("python3 - <<'PY'\n", 1)[1].rsplit("\nPY", 1)[0]
        tree = ast.parse(code)
        compile(tree, "hosted-diagnostic-owner", "exec")
        helpers = ast.Module(body=[node for node in tree.body if isinstance(node, ast.FunctionDef)
                                  and node.name in ("execution_in_window", "validate_core_result")], type_ignores=[])
        namespace = {"re": re}
        exec(compile(helpers, "offline-diagnostic-helpers", "exec"), namespace)
        return namespace

    def test_diagnostic_rejects_late_exit_late_observation_and_unbound_execution(self):
        within = self.diagnostic_helpers()["execution_in_window"]
        end = dt.datetime(2026, 1, 1, tzinfo=dt.timezone.utc)
        earlier = end - dt.timedelta(seconds=1)
        self.assertTrue(within(1000, 180000001000, 179000000, 179500000000, end, earlier, True, False))
        for exited, observed, clock, admitted, backward in (
                (180000002, 180000003000, earlier, True, False),
                (179000000, 180000002000, earlier, True, False),
                (179000000, 179500000000, end, True, False),
                (179000000, 179500000000, earlier, False, False),
                (179000000, 179500000000, earlier, True, True)):
            with self.subTest(exited=exited, observed=observed, admitted=admitted, backward=backward):
                self.assertFalse(within(1000, 180000001000, exited, observed, end, clock, admitted, backward))

    def test_diagnostic_artifact_rejects_unknown_content_types_and_acceptance_claims(self):
        validate = self.diagnostic_helpers()["validate_core_result"]
        digest, image = "a" * 64, "synthetic-pinned-image"
        minimal_failure = {"applicationAcceptance": False, "cloudRelease": False, "scriptSha256": digest, "image": image}
        self.assertEqual(validate(minimal_failure, digest, image), minimal_failure)
        for mutation in ({"rawOutput": "not publishable"}, {"sqlErrors": ["3201"]},
                         {"sqlErrors": [True]}, {"verifyOnlyExitCode": False},
                         {"applicationAcceptance": True}, {"scriptSha256": "b" * 64},
                         {"hostMode": "1001:128:640:123\nadditional output"}):
            with self.subTest(keys=list(mutation)):
                with self.assertRaises(RuntimeError):
                    validate(minimal_failure | mutation, digest, image)

    def test_recovery_diagnostic_is_a_narrow_read_only_push_workflow(self):
        workflow = yaml.safe_load((ROOT / ".github/workflows/cohors-recovery-copy-diagnostic.yml").read_text(encoding="utf-8"))
        self.assertEqual(workflow["on"], {"push": {
            "branches": ["micnhs-cohors-azure-infrastruktur"],
            "paths": [".github/workflows/cohors-recovery-copy-diagnostic.yml", "diagnostics/recovery-host-copy.ps1"]}})
        self.assertEqual(workflow["permissions"], {"contents": "read"})
        self.assertFalse(workflow["concurrency"]["cancel-in-progress"])
        self.assertEqual(set(workflow["jobs"]), {"diagnostic"})
        job = workflow["jobs"]["diagnostic"]
        self.assertEqual(job["runs-on"], "ubuntu-24.04")
        self.assertNotIn("permissions", job)
        package = yaml.safe_load((ROOT / ".github/workflows/cohors-primary-package.yml").read_text(encoding="utf-8"))
        self.assertEqual(package["on"]["push"]["paths"],
                         ["infra/**", "!infra/tests/test_workflow_contract.py", ".github/workflows/cohors-primary-package.yml"])

    def test_infrastructure_binds_existing_acceptance_instead_of_repeating_it(self):
        ci = yaml.safe_load((ROOT / ".github/workflows/cohors-ci.yml").read_text(encoding="utf-8"))
        app = next(step for step in ci["jobs"]["acceptance"]["steps"]
                   if step.get("with", {}).get("path") == "application")
        self.assertEqual(app["with"]["ref"], "${{ github.sha }}")
        self.assertEqual(ci["jobs"]["acceptance"]["if"],
                         "${{ (github.head_ref || github.ref_name) != 'micnhs-cohors-azure-infrastruktur' }}")
        existing = ci["jobs"]["application-source-binding"]
        self.assertEqual(existing["if"],
                         "${{ (github.head_ref || github.ref_name) == 'micnhs-cohors-azure-infrastruktur' }}")
        command = next(step["run"] for step in existing["steps"] if "run" in step)
        self.assertIn("--commit dd112705af2baf6c4e7787794b782f4f8937b1e8", command)
        self.assertIn("--run-id 44355710367", command)
        self.assertNotRegex(command, r"dotnet|Invoke-CohorsCi|gh run rerun|workflow run")

    def test_cloud_credentials_are_only_in_protected_manual_jobs(self):
        workflow = yaml.safe_load((ROOT / ".github/workflows/cohors-deploy.yml").read_text(encoding="utf-8"))
        self.assertEqual(set(workflow["on"]), {"workflow_dispatch"})
        self.assertNotIn("id-token", workflow["permissions"])
        for name, job in workflow["jobs"].items():
            if job.get("permissions", {}).get("id-token") == "write":
                self.assertIn(job["environment"], {"cohors-pilot-preview", "cohors-pilot-deploy"})
                self.assertIn(name, {"preview", "apply"})
        self.assertEqual(workflow["jobs"]["apply"]["needs"], "preview")
        self.assertIn("inputs.operation == 'apply'", workflow["jobs"]["apply"]["if"])
        ci = yaml.safe_load((ROOT / ".github/workflows/cohors-ci.yml").read_text(encoding="utf-8"))
        self.assertNotIn("id-token", ci["permissions"])
        self.assertTrue(all("id-token" not in job.get("permissions", {}) for job in ci["jobs"].values()))

    def test_actions_are_immutable_and_artifacts_do_not_include_credentials(self):
        for path in (ROOT / ".github/workflows").glob("cohors-*.yml"):
            workflow = yaml.safe_load(path.read_text(encoding="utf-8"))
            for job in workflow["jobs"].values():
                for step in job["steps"]:
                    if "uses" in step:
                        self.assertRegex(step["uses"], r"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+@[0-9a-f]{40}$")
                    if step.get("uses", "").startswith("actions/upload-artifact@"):
                        paths = step["with"]["path"].splitlines()
                        if path.name == "cohors-recovery-copy-diagnostic.yml":
                            self.assertEqual(paths, ["${{ runner.temp }}/cohors-recovery-copy-result.json"])
                            self.assertEqual(step["with"]["name"], "cohors-recovery-copy-diagnostic")
                            self.assertEqual(step["with"]["retention-days"], 1)
                            self.assertEqual(step["with"]["if-no-files-found"], "error")
                        elif path.name == "cohors-primary-package.yml":
                            self.assertEqual(paths, ["${{ runner.temp }}/cohors-primary-package/cohors-primary-release.zip"])
                            self.assertNotIn("id-token", workflow["permissions"])
                        elif step["with"].get("name") == "cohors-infrastructure-evidence":
                            self.assertEqual(set(paths), {
                                "${{ runner.temp }}/cohors-iac/" + name for name in (
                                    "main.json", "primary-network.json", "primary.json",
                                    "checkov-main.json", "checkov-primary-network.json", "checkov-primary.json",
                                    "checkov-main.stderr.log", "checkov-primary-network.stderr.log",
                                    "checkov-main.executions.json", "checkov-primary-network.executions.json",
                                    "checkov-primary.executions.json", "checkov-main-input.manifest.json",
                                    "checkov-primary-network-input.manifest.json", "checkov-primary-input.manifest.json",
                                    "checkov-primary.stderr.log", "security-gate.json")})
                        elif step["with"].get("name") == "cohors-existing-application-acceptance":
                            self.assertEqual(paths, ["${{ runner.temp }}/cohors-application-acceptance.json"])
                        else:
                            self.assertEqual(len(paths), 4)
                            self.assertTrue(all(re.search(r"/(acceptance\.trx|e2e-results\.xml|cohors-cockpit\.png|cohors-workbench\.png)$", item) for item in paths))
                        self.assertNotRegex(step["with"]["path"], r"settings|demo-access|trace|storageState|\*\*")


if __name__ == "__main__":
    unittest.main()
