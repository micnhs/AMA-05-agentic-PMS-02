import unittest
from pathlib import Path

import yaml


ROOT = Path(__file__).resolve().parents[2]


class WorkflowContractTests(unittest.TestCase):
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
                        expected = {
                            "${{ runner.temp }}/cohors-test-evidence/" + name
                            for name in (
                                "acceptance.trx", "bootstrap.trx", "e2e-results.xml",
                                "cohors-cockpit.png", "cohors-workbench.png",
                            )
                        }
                        if path.name == "cohors-ci.yml":
                            expected.update(
                                f"${{{{ runner.temp }}}}/cohors-test-evidence/browser-originals/{project}/{name}"
                                for project in (
                                    "preview", "audit-export", "summary-report", "case-documents", "report-prefill",
                                    "information-lifecycle", "eudamed-configuration", "eudamed-dossiers",
                                    "cockpit-calendar", "signal-review", "physical-retention",
                                )
                                for name in ("e2e-results.xml", "owned-resources-terminal.json")
                            )
                        self.assertCountEqual(paths, expected)
                        self.assertNotRegex(step["with"]["path"], r"settings|demo-access|trace|storageState|private-|runtime/|\*")


if __name__ == "__main__":
    unittest.main()
