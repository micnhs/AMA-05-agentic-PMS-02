import re
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
WORKFLOWS = ROOT / ".github" / "workflows"


class WorkflowIsolationTests(unittest.TestCase):
    def test_runner_context_is_used_only_after_job_scheduling(self):
        source = (WORKFLOWS / "cohors-fabric-contracts.yml").read_text(encoding="utf-8")
        before_steps, steps = source.split("    steps:\n", 1)
        self.assertNotRegex(before_steps, r"\$\{\{[^}]*\brunner\.")
        self.assertIn("$env:RUNNER_TEMP", steps)
        self.assertLess(steps.index("$env:GITHUB_ENV"), steps.index("uses: actions/setup-dotnet@"))

    def test_inherited_deployment_cannot_claim_primary_concurrency(self):
        source = (WORKFLOWS / "cohors-deploy.yml").read_text(encoding="utf-8")
        self.assertNotIn("group: cohors-pilot-infrastructure", source)
        group = re.search(r"^  group: (.+)$", source, re.MULTILINE).group(1)
        self.assertTrue(group.startswith("cohors-fabric-disabled-primary-deployment-"))
        self.assertIn("${{ github.ref }}", group)
        for name in ("cohors-deploy.yml", "cohors-ci.yml"):
            inherited = (WORKFLOWS / name).read_text(encoding="utf-8")
            self.assertNotRegex(inherited, r"(?m)^  (push|pull_request|schedule):")
            job_headers = re.findall(r"^  [A-Za-z][\w-]*:\n(.*?)(?=^  [A-Za-z][\w-]*:\n|\Z)",
                                     inherited.split("\njobs:\n", 1)[1], re.MULTILINE | re.DOTALL)
            self.assertTrue(job_headers)
            self.assertTrue(all("if: ${{ false }}" in job for job in job_headers))

    def test_hosted_scope_cannot_silently_expand(self):
        source = (WORKFLOWS / "cohors-fabric-contracts.yml").read_text(encoding="utf-8")
        self.assertIn("branches: [micnhs-cohors-fabric-umsetzung]", source)
        jobs = source.split("\njobs:\n", 1)[1]
        self.assertEqual(["contracts"], re.findall(r"^  ([A-Za-z][\w-]*):\s*$", jobs, re.MULTILINE))
        self.assertEqual(1, len(re.findall(r"^    runs-on:", source, re.MULTILINE)))
        permissions = re.search(r"^permissions:\n((?:  .*\n)+)", source, re.MULTILINE)
        self.assertIsNotNone(permissions)
        self.assertEqual("contents: read", permissions.group(1).strip())
        self.assertNotRegex(jobs, r"(?m)^    permissions:")
        self.assertIn("runs-on: ubuntu-24.04", source)
        self.assertIn("ref: ${{ github.sha }}", source)
        self.assertIn("persist-credentials: false", source)
        self.assertNotRegex(source, r"secrets\.|id-token:|strategy:|services:|upload-artifact@|actions/cache@|azure/login@")
        self.assertNotRegex(source, r"(?m)^  (workflow_dispatch|pull_request|workflow_call|schedule):")


if __name__ == "__main__":
    unittest.main()
