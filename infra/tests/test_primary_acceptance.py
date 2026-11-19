import copy
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).parents[1] / "scripts"))
from primary_acceptance import APPLICATION_JOB, APPLICATION_STEP, application_acceptance, require_application_acceptance
from primary_control import GateError
import primary_release


class PrimaryAcceptanceTests(unittest.TestCase):
    def setUp(self):
        self.run = {"id": 123, "head_sha": "a" * 40, "head_branch": "micnhs-cohors-pilot",
                    "path": ".github/workflows/cohors-ci.yml", "repository": {"full_name": "example/cohors"},
                    "event": "push", "run_attempt": 1, "status": "completed", "conclusion": "success"}
        self.job = {"id": 456, "run_id": 123, "run_attempt": 1, "head_sha": "a" * 40,
                    "name": APPLICATION_JOB, "status": "completed", "conclusion": "success",
                    "started_at": "2026-11-18T10:00:00Z", "completed_at": "2026-11-18T10:10:00Z",
                    "steps": [{"name": APPLICATION_STEP, "number": 7, "status": "completed", "conclusion": "success",
                               "started_at": "2026-11-18T10:01:00Z", "completed_at": "2026-11-18T10:09:00Z"}]}
        self.calls = []

    def fetch(self, path):
        self.calls.append(path)
        if path.endswith("/runs/123"):
            return copy.deepcopy(self.run)
        self.assertTrue(path.endswith("/runs/123/attempts/1/jobs?per_page=100"))
        return {"total_count": 1, "jobs": [copy.deepcopy(self.job)]}

    def test_reads_exact_attempt_without_launching_another_test_lane(self):
        receipt = require_application_acceptance("example/cohors", 123, "a" * 40, self.fetch)
        self.assertTrue(receipt["passed"])
        self.assertFalse(receipt["testsRerun"])
        self.assertFalse(receipt["cloudReleased"])
        self.assertEqual(receipt["runAttempt"], 1)
        self.assertEqual(receipt["contract"], "cohors-existing-application-acceptance-2")
        self.assertFalse(receipt["securityQualified"])
        self.assertFalse(receipt["releaseQualified"])
        self.assertEqual(len(self.calls), 3)

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
        self.run["conclusion"] = "cancelled"
        self.assertFalse(application_acceptance("example/cohors", 123, "a" * 40, self.fetch)["passed"])

    def test_source_workflow_or_attempt_substitution_is_rejected(self):
        original_run, original_job = copy.deepcopy(self.run), copy.deepcopy(self.job)
        for target, key, value in (
            ("run", "head_sha", "b" * 40), ("run", "head_branch", "main"),
            ("run", "event", "workflow_dispatch"), ("run", "path", "another.yml"),
            ("job", "run_id", 124), ("job", "run_attempt", 2), ("job", "head_sha", "b" * 40),
            ("run", "run_attempt", 2),
        ):
            with self.subTest(target=target, key=key):
                self.run, self.job = copy.deepcopy(original_run), copy.deepcopy(original_job)
                getattr(self, target)[key] = value
                with self.assertRaises(GateError):
                    application_acceptance("example/cohors", 123, "a" * 40, self.fetch)

    def test_completed_app_job_does_not_inherit_the_old_infra_job_failure(self):
        self.run["conclusion"] = "failure"
        receipt = require_application_acceptance("example/cohors", 123, "a" * 40, self.fetch)
        self.assertTrue(receipt["passed"])
        self.assertEqual(receipt["runConclusion"], "failure")
        self.assertEqual(receipt["qualificationScope"], "original-application-job-and-test-step")
        self.assertFalse(receipt["releaseQualified"])

    def test_incomplete_or_failed_app_job_stays_negative_even_with_a_successful_test_step(self):
        for field, value in (("conclusion", "failure"), ("conclusion", "cancelled"), ("conclusion", "skipped"),
                             ("status", "in_progress"), ("completed_at", None), ("completed_at", "2026-11-18T09:00:00Z")):
            original = copy.deepcopy(self.job)
            self.job[field] = value
            with self.subTest(field=field, value=value):
                self.assertFalse(application_acceptance("example/cohors", 123, "a" * 40, self.fetch)["passed"])
            self.job = original
        self.job["steps"][0]["completed_at"] = None
        self.assertFalse(application_acceptance("example/cohors", 123, "a" * 40, self.fetch)["passed"])
        self.job["steps"] = []
        with self.assertRaisesRegex(GateError, "original test step"):
            application_acceptance("example/cohors", 123, "a" * 40, self.fetch)

    def test_app_only_success_cannot_release_without_current_infra_and_exact_package(self):
        self.run["conclusion"] = "failure"
        binding = {"repository": "example/cohors", "validationRunId": 999, "infrastructureCommit": "b" * 40,
                   "applicationValidationRunId": 123, "applicationCommit": "a" * 40}
        infra_run = {"id": 999, "head_sha": "b" * 40, "head_branch": "micnhs-cohors-azure-infrastruktur",
                     "path": ".github/workflows/cohors-ci.yml", "repository": {"full_name": "example/cohors"},
                     "event": "push", "run_attempt": 1, "status": "completed", "conclusion": "failure"}
        jobs = {"total_count": 3, "jobs": [
            {"id": index, "run_id": 999, "head_sha": "b" * 40, "run_attempt": 1, "status": "completed",
             "name": name, "conclusion": conclusion} for index, (name, conclusion) in enumerate((
                ("Bicep and local security gates", "success"), ("Bound application acceptance (existing run)", "success"),
                (APPLICATION_JOB, "skipped")), start=1)]}

        def fetch(path):
            if path.endswith("/runs/999"):
                return copy.deepcopy(infra_run)
            if path.endswith("/runs/999/attempts/1/jobs?per_page=100"):
                return copy.deepcopy(jobs)
            return self.fetch(path)

        with patch.object(primary_release, "qualified_artifact") as package, patch.object(primary_release, "github_json", side_effect=fetch):
            with self.assertRaisesRegex(GateError, "security/native/application"):
                primary_release.require_hosted_qualification(binding)
            infra_run["conclusion"] = "success"
            primary_release.require_hosted_qualification(binding)
            package.assert_called_with(binding)
            jobs["jobs"][0]["conclusion"] = "failure"
            with self.assertRaisesRegex(GateError, "qualification jobs"):
                primary_release.require_hosted_qualification(binding)
            jobs["jobs"][0]["conclusion"] = "success"
            jobs["jobs"][0]["run_attempt"] = 2
            with self.assertRaisesRegex(GateError, "qualification jobs"):
                primary_release.require_hosted_qualification(binding)
            jobs["jobs"][0]["run_attempt"] = 1
            with patch.object(primary_release, "github_json",
                              side_effect=[infra_run, jobs, {**infra_run, "run_attempt": 2}]):
                with self.assertRaisesRegex(GateError, "changed during readback"):
                    primary_release.require_hosted_qualification(binding)
            package.side_effect = GateError("Exact package missing")
            with self.assertRaisesRegex(GateError, "Exact package"):
                primary_release.require_hosted_qualification(binding)

    def test_attempt_change_during_readback_cannot_relabel_an_old_job(self):
        reads = 0

        def swapping_fetch(path):
            nonlocal reads
            result = self.fetch(path)
            if path.endswith("/runs/123"):
                reads += 1
                if reads == 2:
                    result["run_attempt"] = 2
            return result

        with self.assertRaisesRegex(GateError, "changed during readback"):
            application_acceptance("example/cohors", 123, "a" * 40, swapping_fetch)


if __name__ == "__main__":
    unittest.main()
