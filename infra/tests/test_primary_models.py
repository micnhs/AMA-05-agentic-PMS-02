import sys
from pathlib import Path
import copy
import datetime as dt
import json
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts"))
from primary_readiness import model_candidate, collect_model_candidates
from primary_control import GateError, digest, file_hash
from primary_model_gate import TASKS, require_model_acceptance


class ModelCandidatesTests(unittest.TestCase):
    def test_strong_models_are_not_filtered_by_price_or_name_allowlist(self):
        row = {"kind": "AIServices", "skuName": "S0", "model": {
            "name": "new-frontier-family", "version": "2026-01-01", "format": "OpenAI",
            "capabilities": {"chatCompletion": "true"}, "skus": [{"name": "Standard"}]}}
        candidate = model_candidate(row, "francecentral")
        self.assertEqual(candidate["model"]["name"], "new-frontier-family")
        self.assertTrue(candidate["regionalStandardCatalogued"])
        self.assertFalse(candidate["clinicalQualityProven"])
        self.assertFalse(candidate["deployableCapacityProven"])

    def test_global_or_policy_blocked_models_are_not_regional_standard(self):
        row = {"model": {"format": "OpenAI", "capabilities": {"chatCompletion": True},
                         "skus": [{"name": "GlobalStandard"}, {"name": "ProvisionedManaged"}]}}
        candidate = model_candidate(row, "francecentral")
        self.assertFalse(candidate["regionalStandardCatalogued"])
        self.assertTrue(candidate["provisionedManagedPolicyBlocked"])
        self.assertIsNone(model_candidate({"model": {"capabilities": {"embeddings": True}}}, "francecentral"))

    def test_region_expansion_is_explicit_and_eu_member_state_only(self):
        for regions in ([], ["eastus"], ["norwayeast"], ["switzerlandnorth"],
                        ["francecentral", "francecentral"]):
            with self.subTest(regions=regions), self.assertRaises(GateError):
                collect_model_candidates(None, {}, regions, {})


class ModelAcceptanceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.now = dt.datetime(2026, 11, 18, 12, tzinfo=dt.timezone.utc)
        self.model = {"name": "candidate", "version": "2026-01-01", "deploymentName": "comparison",
                      "sku": "Standard", "region": "francecentral"}
        self.manifest = {"bindingHash": "b" * 64, "model": self.model,
                         "expiresAtUtc": "2026-11-25T12:00:00Z",
                         "releaseBundle": {"applicationCommit": "a" * 40},
                         "actors": [{"objectId": "domain-reviewer", "roles": ["MedicalSafetyReviewer"]}]}
        self.observation = {"deploymentId": "/own/test/model", "model": self.model}
        self.outputs = {"phase-initialize": {"modelObservation": self.observation}}
        criteria = self.write("criteria.json", {"testFixture": True, "scope": "SyntheticTestsOnly"})
        self.review = {"reviewerObjectId": "domain-reviewer", "accepted": True, "scope": "SyntheticPilotOnly",
                       "criteria": {task: criteria for task in TASKS}}
        self.evaluation = {"executionMode": "FoundryLive", "applicationCommit": "a" * 40,
                           "candidate": self.model, "modelReadbackSha256": digest(self.observation),
                           "promptVersion": "test-prompt",
                           "taskResults": [{"task": task, "sampleCount": 1, "falsePositives": 0,
                                            "falseNegatives": 0, "unsupportedClaims": 0, "invalidOutputs": 0,
                                            "criteriaSha256": criteria["sha256"]} for task in sorted(TASKS)]}
        self.acceptance = {"contract": "cohors-primary-model-acceptance-1", "bindingHash": "b" * 64,
                           "applicationCommit": "a" * 40, "candidate": self.model, "accepted": True,
                           "runtimeProfileMode": "SingleSharedQualifiedProfile", "promptVersion": "test-prompt",
                           "reviewedAtUtc": "2026-11-18T11:00:00Z", "expiresAtUtc": "2026-11-25T12:00:00Z",
                           "acceptedTasks": sorted(TASKS), "automaticCheaperFallback": False,
                           "unqualifiedTaskRouting": False, "guaranteesZeroErrors": False,
                           "clinicalValidationClaim": False, "modelReadbackSha256": digest(self.observation)}

    def write(self, name, value):
        path = self.root / name
        path.write_text(json.dumps(value), encoding="utf-8")
        return {"file": name, "sha256": file_hash(path)}

    def approval(self):
        self.acceptance["evaluationReport"] = self.write("evaluation.json", self.evaluation)
        self.review["evaluationReportSha256"] = self.acceptance["evaluationReport"]["sha256"]
        self.acceptance["domainReview"] = self.write("review.json", self.review)
        return {"modelAcceptance": self.write("acceptance.json", self.acceptance)}

    def check(self, phase="runtime"):
        return require_model_acceptance(self.manifest, phase, self.approval(), self.root,
                                        self.outputs, now=self.now)

    def test_resource_preparation_does_not_require_a_future_live_acceptance(self):
        self.assertIsNone(require_model_acceptance(self.manifest, "foundation", {}, self.root, {}, now=self.now))
        self.assertIsNone(require_model_acceptance(self.manifest, "initialize", {}, self.root, {}, now=self.now))
        with self.assertRaises(GateError):
            require_model_acceptance(self.manifest, "runtime", {}, self.root, {}, now=self.now)
        self.assertFalse(self.check()["clinicalValidationClaim"])

    def test_replay_and_missing_error_metrics_never_become_live_quality(self):
        original = copy.deepcopy(self.evaluation)
        for mode in ("Replay", "Offline", None):
            with self.subTest(mode=mode), self.assertRaises(GateError):
                self.evaluation["executionMode"] = mode
                self.check()
        self.evaluation = original
        del self.evaluation["taskResults"][0]["falseNegatives"]
        with self.assertRaisesRegex(GateError, "missing is not zero"):
            self.check()

    def test_a_different_model_or_unqualified_cheap_routing_is_rejected(self):
        self.evaluation["candidate"] = {**self.model, "version": "different"}
        with self.assertRaises(GateError):
            self.check()
        self.evaluation["candidate"] = self.model
        self.acceptance["automaticCheaperFallback"] = True
        with self.assertRaises(GateError):
            self.check()

    def test_infrastructure_owner_is_not_assumed_to_be_a_domain_reviewer(self):
        self.review["reviewerObjectId"] = "infrastructure-owner"
        with self.assertRaisesRegex(GateError, "domain reviewer"):
            self.check()

    def test_mutated_evidence_and_universal_zero_error_claim_are_rejected(self):
        approval = self.approval()
        (self.root / "evaluation.json").write_text("{}", encoding="utf-8")
        with self.assertRaises(GateError):
            require_model_acceptance(self.manifest, "runtime", approval, self.root, self.outputs, now=self.now)
        self.acceptance["guaranteesZeroErrors"] = True
        with self.assertRaisesRegex(GateError, "universal zero errors"):
            self.check()

    def test_malformed_nested_evidence_is_an_explicit_gate_failure(self):
        for field in ("acceptedTasks", "taskResults", "criteria"):
            with self.subTest(field=field):
                original = copy.deepcopy((self.acceptance, self.evaluation, self.review))
                if field == "acceptedTasks":
                    self.acceptance[field][0] = {}
                elif field == "taskResults":
                    self.evaluation[field][0]["task"] = []
                else:
                    self.review[field] = []
                with self.assertRaises(GateError):
                    self.check()
                self.acceptance, self.evaluation, self.review = original


if __name__ == "__main__":
    unittest.main()
