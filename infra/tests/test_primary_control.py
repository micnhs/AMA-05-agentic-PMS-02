import importlib.util
import json
from pathlib import Path
import tempfile
import unittest


SPEC = importlib.util.spec_from_file_location(
    "primary_control", Path(__file__).parents[1] / "scripts" / "primary_control.py")
control = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(control)


class PrimaryControlTests(unittest.TestCase):
    def test_journal_preserves_unresolved_intent_and_refuses_duplicate_post(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "journal.json"
            with control.Journal(path, "a" * 64) as journal:
                self.assertTrue(journal.begin("api-app", "POST", "graph/applications", {"displayName": "owned"}))
            with control.Journal(path, "a" * 64) as resumed:
                with self.assertRaisesRegex(control.GateError, "unresolved intent"):
                    resumed.begin("api-app", "POST", "graph/applications", {"displayName": "owned"})
                with self.assertRaisesRegex(control.GateError, "payload changed"):
                    resumed.begin("api-app", "POST", "graph/applications", {"displayName": "other"})

    def test_completed_checkpoint_is_idempotent_and_binding_cannot_change(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "journal.json"
            with control.Journal(path, "a" * 64) as journal:
                journal.begin("group", "POST", "graph/groups", {"mailEnabled": False})
                journal.complete("group", {"objectId": "created-object"})
            with control.Journal(path, "a" * 64) as resumed:
                self.assertFalse(resumed.begin("group", "POST", "graph/groups", {"mailEnabled": False}))
                self.assertEqual(resumed.state["outputs"]["group"]["objectId"], "created-object")
            with self.assertRaisesRegex(control.GateError, "different immutable"):
                with control.Journal(path, "b" * 64):
                    pass

    def test_modified_historical_event_is_rejected(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "journal.json"
            with control.Journal(path, "a" * 64) as journal:
                journal.begin("effect", "PUT", "bound-target", {})
            value = json.loads(path.read_text())
            value["events"][0]["details"]["target"] = "foreign-target"
            path.write_text(json.dumps(value))
            with self.assertRaisesRegex(control.GateError, "hash chain"):
                with control.Journal(path, "a" * 64):
                    pass

    def test_mutation_and_redirect_targets_fail_before_token_access(self):
        class NoCredential:
            def token(self, resource):
                raise AssertionError("Must not acquire a token for an inadmissible request.")
        client = control.Azure(NoCredential())
        for method, url in (("PUT", control.ARM + "/anything"), ("GET", "https://example.com/"),
                            ("GET", "https://graph.microsoft.com@evil.example/")):
            with self.subTest(method=method, url=url), self.assertRaises(control.GateError):
                client.request(method, url)

    def test_claim_projection_never_includes_other_claims(self):
        import base64
        payload = base64.urlsafe_b64encode(json.dumps(
            {"oid": "bound", "tid": "tenant", "name": "not-output", "secret": "not-output"}).encode()).decode().rstrip("=")
        result = control.jwt_metadata(f"header.{payload}.signature")
        self.assertEqual(result["oid"], "bound")
        self.assertNotIn("name", result)
        self.assertNotIn("secret", result)

    def test_control_audiences_are_explicit_observations_not_default_scope_derivations(self):
        binding = {"tenantId": "11111111-2222-4333-8444-555555555555"}
        issuer = "https://sts.windows.net/" + binding["tenantId"] + "/"
        observed = {"armAudience": "https://management.core.windows.net/", "armIssuer": issuer,
                    "graphAudience": "00000003-0000-0000-c000-000000000000", "graphIssuer": issuer}
        self.assertEqual(control.control_claims(binding, observed, control.ARM + "/"),
                         (observed["armAudience"], issuer))
        self.assertEqual(control.control_claims(binding, observed, control.GRAPH + "/"),
                         (observed["graphAudience"], issuer))
        for value in (None, {}, {**observed, "graphAudience": control.GRAPH + "/.default"},
                      {**observed, "graphIssuer": "https://sts.windows.net/another-tenant/"}):
            with self.subTest(value=value), self.assertRaises(control.GateError):
                control.control_claims(binding, value, control.GRAPH + "/")


if __name__ == "__main__":
    unittest.main()
