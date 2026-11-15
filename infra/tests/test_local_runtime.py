import copy
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import Mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts"))
from local_runtime import Harness, RuntimeFailure, filtered_json, memory_bytes, require_local_context, verify_resume


class LocalRuntimeTests(unittest.TestCase):
    def test_resume_needs_redelivery_and_exactly_one_unchanged_effect(self):
        invocations = [{"status": "Completed", "mode": "Replay", "model": "replay-2026.1",
                        "role": role, "inputHash": str(index) * 64}
                       for index, role in enumerate(("Ingestion", "CausalityRisk", "EvidenceAssurance"))]
        before = {"receipts": 1, "drafts": 1, "starts": 1, "assessments": 1, "attempts": 1, "completed": True,
                  "invocations": invocations}
        transport = {"held": True, "calls": 1, "closed": False, "agentCalls": copy.deepcopy(invocations)}
        resumed = transport | {"calls": 2, "closed": True}
        verify_resume(before, before, transport, resumed)
        for key, invalid in (("receipts", 2), ("drafts", 2), ("assessments", 0), ("attempts", 2), ("completed", False)):
            with self.subTest(key=key):
                with self.assertRaises(RuntimeFailure):
                    verify_resume(before, before | {key: invalid}, transport, resumed)
        for mutation in ({"calls": 1}, {"closed": False}, {"agentCalls": resumed["agentCalls"] * 2}):
            with self.subTest(mutation=mutation):
                with self.assertRaises(RuntimeFailure):
                    verify_resume(before, before, transport, resumed | mutation)
        live = copy.deepcopy(before)
        live["invocations"][0]["mode"] = "Live"
        with self.assertRaises(RuntimeFailure):
            verify_resume(before, live, transport, resumed)
        repeated_role = copy.deepcopy(before)
        repeated_role["invocations"][1]["role"] = "Ingestion"
        with self.assertRaisesRegex(RuntimeFailure, "distinct assessment roles"):
            verify_resume(repeated_role, repeated_role, transport, resumed)
        wrong_transport = copy.deepcopy(transport)
        wrong_transport["agentCalls"][0]["inputHash"] = "f" * 64
        with self.assertRaisesRegex(RuntimeFailure, "persisted SQL invocations"):
            verify_resume(before, before, wrong_transport, wrong_transport | {"calls": 2, "closed": True})

    def test_evidence_rejects_secrets_and_auth_fields(self):
        self.assertEqual(json.loads(filtered_json({"status": "blocked"}, ["synthetic-secret"])), {"status": "blocked"})
        for result in ({"message": "synthetic-secret"}, {"password": "redacted"}, {"Authorization": "Bearer value"}):
            with self.assertRaises(RuntimeFailure):
                filtered_json(result, ["synthetic-secret"])

    def test_local_guard_and_separate_fresh_directories(self):
        with tempfile.TemporaryDirectory(prefix="cohors-t12-contract-") as directory:
            root = Path(directory)
            with self.assertRaisesRegex(RuntimeFailure, "not GitHub Actions"):
                require_local_context("true")
            require_local_context(None)
            with self.assertRaisesRegex(RuntimeFailure, "separate"):
                Harness(root / "work", root / "work" / "evidence")
            with self.assertRaisesRegex(RuntimeFailure, "new workspace"):
                Harness(root, root / "evidence")

    def test_capacity_units_are_not_silent_zeroes(self):
        self.assertEqual(memory_bytes("1.5GiB"), 1.5 * 1024**3)
        self.assertEqual(memory_bytes("256MiB"), 256 * 1024**2)
        with self.assertRaises(RuntimeFailure):
            memory_bytes("unavailable")

    def test_filtered_diagnostics_never_include_credentials_or_raw_logs(self):
        with tempfile.TemporaryDirectory(prefix="cohors-t12-diagnostics-") as directory:
            root = Path(directory)
            harness = Harness(root / "work", root / "evidence")
            harness.prohibited = ["synthetic-private-value"]
            messages = harness.diagnostics("Routine raw host log\n"
                "Sqlcmd: Error: synthetic-private-value\nAuthorization: Bearer synthetic-private-value\n"
                "Workflow readiness failed: RpcException.\n"
                "Grpc.Core.RpcException: StatusCode=\"Unavailable\", Detail=\"synthetic-private-value\"\n"
                "System.Net.Http.HttpRequestException: http://127.0.0.1/?code=not-in-inventory\n"
                "System.InvalidOperationException: password=synthetic-private-value")
            self.assertIn("Workflow readiness failed (RpcException).", messages)
            self.assertIn("Exception type: Grpc.Core.RpcException", messages)
            self.assertIn("RPC/HTTP status: Unavailable", messages)
            self.assertIn("Sqlcmd: Error: [REDACTED]", messages)
            self.assertFalse(any("Detail=" in message or "Routine raw" in message for message in messages))
            self.assertFalse(any("not-in-inventory" in message for message in messages))
            filtered_json({"diagnostics": messages}, harness.prohibited)

    def test_readiness_timeout_retains_the_real_non_success_status(self):
        with tempfile.TemporaryDirectory(prefix="cohors-t12-readiness-") as directory:
            root = Path(directory)
            harness = Harness(root / "work", root / "evidence")
            harness.inspect = Mock(return_value={"Running": True})
            harness.driver = Mock(return_value={"code": 503, "status": "not-ready"})
            with self.assertRaisesRegex(RuntimeFailure, "no success inferred"):
                harness.ready("workflows", seconds=0)
            self.assertEqual(harness.evidence["readiness"]["workflows"]["code"], 503)
            self.assertEqual(harness.evidence["readiness"]["workflows"]["status"], "not-ready")
            self.assertEqual(harness.evidence["status"], "blocked")

    def test_cleanup_refuses_a_container_with_another_owner(self):
        with tempfile.TemporaryDirectory(prefix="cohors-t12-cleanup-") as directory:
            root = Path(directory)
            harness = Harness(root / "work", root / "evidence")
            harness.containers["api"] = "a" * 64
            harness.command = Mock(return_value=subprocess.CompletedProcess([], 0, "another-owner\n", ""))
            harness.cleanup()
            self.assertEqual(harness.command.call_count, 1)
            self.assertIn("inspect", harness.command.call_args.args[0])
            self.assertFalse(harness.evidence["cleanup"]["complete"])
            self.assertEqual(harness.evidence["status"], "blocked")

    def test_cleanup_does_not_treat_an_unreachable_daemon_as_an_absent_image(self):
        with tempfile.TemporaryDirectory(prefix="cohors-t12-cleanup-") as directory:
            root = Path(directory)
            harness = Harness(root / "work", root / "evidence")
            harness.tags = [f"{harness.prefix}-api:local"]
            harness.command = Mock(return_value=subprocess.CompletedProcess([], 1, "", "failed to connect to the docker API"))
            harness.cleanup()
            self.assertFalse(harness.evidence["cleanup"]["complete"])
            self.assertIn("image tag state unavailable", harness.evidence["cleanup"]["failures"])
            harness.command = Mock(return_value=subprocess.CompletedProcess([], 1, "", "Error response from daemon: No such image"))
            harness.cleanup()
            self.assertTrue(harness.evidence["cleanup"]["complete"])


if __name__ == "__main__":
    unittest.main()
