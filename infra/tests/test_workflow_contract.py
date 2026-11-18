import re
import unittest
from pathlib import Path

import yaml


ROOT = Path(__file__).resolve().parents[2]


class WorkflowContractTests(unittest.TestCase):
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
                        if path.name == "cohors-primary-package.yml":
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
