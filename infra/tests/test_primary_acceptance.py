import copy
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).parents[1] / "scripts"))
from primary_acceptance import APPLICATION_JOB, APPLICATION_STEP, application_acceptance, require_application_acceptance
from primary_control import GateError


class PrimaryAcceptanceTests(unittest.TestCase):
    def setUp(self):
        self.run = {"id": 123, "head_sha": "a" * 40, "head_branch": "micnhs-cohors-pilot",
                    "path": ".github/workflows/cohors-ci.yml", "repository": {"full_name": "example/cohors"},
                    "event": "push", "run_attempt": 2, "status": "completed", "conclusion": "success"}
        self.job = {"id": 456, "run_id": 123, "run_attempt": 2, "head_sha": "a" * 40,
                    "name": APPLICATION_JOB, "status": "completed", "conclusion": "success",
                    "steps": [{"name": APPLICATION_STEP, "status": "completed", "conclusion": "success"}]}
        self.calls = []

    def fetch(self, path):
        self.calls.append(path)
        if path.endswith("/runs/123"):
            return copy.deepcopy(self.run)
        self.assertTrue(path.endswith("/runs/123/attempts/2/jobs?per_page=100"))
        return {"total_count": 1, "jobs": [copy.deepcopy(self.job)]}

    def test_reads_exact_attempt_without_launching_another_test_lane(self):
        receipt = require_application_acceptance("example/cohors", 123, "a" * 40, self.fetch)
        self.assertTrue(receipt["passed"])
        self.assertFalse(receipt["testsRerun"])
        self.assertFalse(receipt["cloudReleased"])
        self.assertEqual(receipt["runAttempt"], 2)
        self.assertEqual(len(self.calls), 2)

    def test_failed_skipped_or_pending_test_result_remains_negative(self):
        for value in ("failure", "skipped", "cancelled", None):
            with self.subTest(value=value):
                self.job["steps"][0]["conclusion"] = value
                receipt = application_acceptance("example/cohors", 123, "a" * 40, self.fetch)
                self.assertFalse(receipt["passed"])
                self.assertEqual(receipt["testStepConclusion"], value)
                with self.assertRaisesRegex(GateError, "packaging cannot waive"):
                    require_application_acceptance("example/cohors", 123, "a" * 40, self.fetch)
        self.job["steps"][0]["conclusion"] = "success"
        self.run["conclusion"] = "failure"
        self.assertFalse(application_acceptance("example/cohors", 123, "a" * 40, self.fetch)["passed"])

    def test_source_workflow_or_attempt_substitution_is_rejected(self):
        original_run, original_job = copy.deepcopy(self.run), copy.deepcopy(self.job)
        for target, key, value in (
            ("run", "head_sha", "b" * 40), ("run", "head_branch", "main"),
            ("run", "event", "workflow_dispatch"), ("run", "path", "another.yml"),
            ("job", "run_id", 124), ("job", "run_attempt", 1), ("job", "head_sha", "b" * 40),
        ):
            with self.subTest(target=target, key=key):
                self.run, self.job = copy.deepcopy(original_run), copy.deepcopy(original_job)
                getattr(self, target)[key] = value
                with self.assertRaises(GateError):
                    application_acceptance("example/cohors", 123, "a" * 40, self.fetch)


if __name__ == "__main__":
    unittest.main()
