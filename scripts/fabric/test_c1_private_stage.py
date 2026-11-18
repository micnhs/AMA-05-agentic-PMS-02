import base64
from contextlib import redirect_stderr, redirect_stdout
from datetime import datetime, timedelta, timezone
from email.message import Message
import io
import json
import os
from pathlib import Path
import signal
import socket
import threading
import unittest
from unittest.mock import Mock, patch

import c1_package as package
import c1_private_stage as stage
import test_c1_package as package_tests


class C1PrivateStageTests(unittest.TestCase):
    def setUp(self):
        self.package_fixture = package_tests.C1PackageTests()
        self.package_fixture.setUp()
        self.addCleanup(self.package_fixture.tearDown)
        self.root = self.package_fixture.root
        self.now = datetime(2030, 1, 1, tzinfo=timezone.utc)
        for module, name in ((package, "c1_package.py"), (stage, "c1_private_stage.py")):
            (self.package_fixture.source / "scripts" / "fabric" / name).write_bytes(Path(module.__file__).read_bytes())
        self.archive = self.package_fixture.outer().read_bytes()
        prefix = "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/cohors-test-c1/providers/"
        self.profile = {
            "contract": stage.CONTRACT, "deploymentBindingHash": "b" * 64,
            "vmResourceId": prefix + "Microsoft.Compute/virtualMachines/cohors-test-probe",
            "storageAccountResourceId": prefix + "Microsoft.Storage/storageAccounts/cohorstestst",
            "tenantId": "00000000-0000-0000-0000-000000000002",
            "managedIdentityClientId": "00000000-0000-0000-0000-000000000003",
            "managedIdentityObjectId": "00000000-0000-0000-0000-000000000004",
            "expectedIssuer": "https://sts.windows.net/00000000-0000-0000-0000-000000000002/",
            "privateAddresses": ["10.40.1.4"], "privatePathReviewSha256": "c" * 64,
            "sourceCommit": self.package_fixture.commit, "runId": self.package_fixture.run_id, "runAttempt": "1",
            "outerArchiveSha256": package.digest(self.archive), "outerArchiveLength": len(self.archive),
            "receiverSha256": package.digest(Path(stage.__file__).read_bytes()),
            "verifierSha256": package.digest(Path(package.__file__).read_bytes()),
            "expiresAtUtc": "2030-01-01T00:10:00Z", "maxDurationSeconds": 60,
        }
        self.destination = self.root / "verified-worker"

    def token(self, changes=None, header=None):
        claims = {
            "aud": stage.STORAGE_AUDIENCE, "iss": self.profile["expectedIssuer"], "tid": self.profile["tenantId"],
            "oid": self.profile["managedIdentityObjectId"], "appid": self.profile["managedIdentityClientId"],
            "xms_mirid": self.profile["vmResourceId"], "idtyp": "app",
            "nbf": int(self.now.timestamp()) - 60, "exp": int(self.now.timestamp()) + 600,
        }
        claims.update(changes or {})
        def segment(value):
            return base64.urlsafe_b64encode(package.encoded(value)).decode().rstrip("=")
        return {"resource": stage.STORAGE_AUDIENCE, "token_type": "Bearer", "expires_on": str(claims["exp"]),
                "access_token": segment(header or {"alg": "RS256"}) + "." + segment(claims) + ".synthetic"}

    def budget(self):
        return stage.Budget(self.profile, clock=lambda: self.now)

    def fake_receiver(self, profile, host, budget):
        self.assertEqual("cohorstestst.blob.core.windows.net", host)
        receiver = Mock()
        receiver.__enter__ = Mock(return_value=receiver)
        receiver.__exit__ = Mock(return_value=False)
        receiver.token.return_value = "in-memory-test-token"
        def download(token, output):
            self.assertEqual("in-memory-test-token", token)
            output.write(self.archive)
            return {"resolvedAddresses": ["10.40.1.4"], "connectedAddress": "10.40.1.4"}
        receiver.download.side_effect = download
        self.receiver = receiver
        return receiver

    def test_verify_only_worker_checks_source_tools_and_every_package_file(self):
        result = stage.stage(self.profile, self.destination, self.fake_receiver, clock=lambda: self.now)
        self.assertEqual("VerifiedNotExecuted", result["status"])
        self.assertFalse(result["payloadExecuted"])
        self.assertEqual(self.profile["outerArchiveSha256"], result["outerArchiveSha256"])
        self.assertEqual(b"synthetic-host-not-executed",
                         (self.destination / "runtimes" / "linux-x64" / "Cohors.Api").read_bytes())
        self.assertFalse((self.destination / "transport.zip").exists())
        self.assertEqual([], list(self.root.glob(".c1-stage-*")))
        self.receiver.token.assert_called_once_with()
        self.receiver.download.assert_called_once()
        factory = Mock()
        with self.assertRaisesRegex(stage.StageError, "new_destination"):
            stage.stage(self.profile, self.destination, factory, clock=lambda: self.now)
        factory.assert_not_called()

    def test_expiry_secret_fields_resource_drift_and_limits_fail_before_network(self):
        for change in (
            {"expiresAtUtc": "2029-01-01T00:00:00Z"}, {"userBearer": "never-transferred"},
            {"blobUrl": "https://public.invalid/payload?sig=never-transferred"},
            {"maxDurationSeconds": 301}, {"maxDurationSeconds": True},
            {"outerArchiveLength": package.MAX_ARCHIVE + 1},
            {"storageAccountResourceId": self.profile["storageAccountResourceId"].replace("cohors-test-c1", "foreign")},
            {"privateAddresses": ["10.40.1.4", "127.0.0.1"]}, {"privateAddresses": ["169.254.169.254"]},
            {"privateAddresses": ["192.0.2.1"]}, {"privateAddresses": ["10.40.1.4", "10.40.1.4"]},
            {"receiverSha256": "d" * 64}, {"verifierSha256": "d" * 64},
        ):
            with self.subTest(change=tuple(change)):
                factory = Mock()
                with self.assertRaises(stage.StageError):
                    stage.stage({**self.profile, **change}, self.destination, factory, clock=lambda: self.now)
                factory.assert_not_called()
                self.assertFalse(self.destination.exists())

    def test_imds_metadata_refuses_delegated_wrong_caller_scope_vm_or_lifetime(self):
        valid = self.token()
        self.assertEqual(valid["access_token"], stage.token_metadata(valid, self.profile, self.now))
        for change in (
            {"scp": ""}, {"scp": "user_impersonation"}, {"idtyp": "user"},
            {"aud": "https://management.azure.com/"}, {"aud": [stage.STORAGE_AUDIENCE]},
            {"oid": self.profile["managedIdentityClientId"]},
            {"appid": self.profile["managedIdentityObjectId"]},
            {"azp": self.profile["managedIdentityObjectId"]},
            {"tid": self.profile["managedIdentityClientId"]}, {"iss": "https://wrong.invalid/"},
            {"xms_mirid": self.profile["vmResourceId"] + "-foreign"},
            {"exp": int(self.now.timestamp())}, {"nbf": int(self.now.timestamp()) + 1},
        ):
            with self.subTest(change=tuple(change)), self.assertRaises(stage.StageError):
                stage.token_metadata(self.token(change), self.profile, self.now)
        with self.assertRaises(stage.StageError):
            stage.token_metadata(self.token(header={"alg": "none"}), self.profile, self.now)

    def test_public_mixed_or_unreviewed_dns_never_reaches_token_acquisition(self):
        for addresses in (["8.8.8.8"], ["10.40.1.4", "8.8.8.8"], ["10.40.1.5"], []):
            answers = [(socket.AF_INET, socket.SOCK_STREAM, 6, "", (value, 443)) for value in addresses]
            with self.subTest(addresses=addresses), patch.object(socket, "getaddrinfo", return_value=answers), \
                    patch.object(stage, "PrivateBlobConnection") as connection, \
                    patch.object(stage.AzureReceiver, "token") as token:
                with self.assertRaisesRegex(stage.StageError, "private_dns"):
                    with stage.AzureReceiver(self.profile, "cohorstestst.blob.core.windows.net", self.budget()):
                        self.fail("A public or unreviewed route cannot enter the receiver")
                connection.assert_not_called()
                token.assert_not_called()

    def test_tls_uses_bound_private_socket_and_original_fqdn_without_second_dns_or_proxy(self):
        raw, tls = Mock(), Mock()
        raw.getpeername.return_value = ("10.40.1.4", 443)
        context = Mock()
        context.wrap_socket.return_value = tls
        with patch.dict(os.environ, {"HTTPS_PROXY": "http://never-used.invalid", "SSL_CERT_FILE": "", "SSL_CERT_DIR": ""}), \
                patch.object(stage.ssl, "create_default_context", return_value=context), \
                patch.object(socket, "socket", return_value=raw), patch.object(socket, "getaddrinfo") as resolver:
            connection = stage.PrivateBlobConnection("cohorstestst.blob.core.windows.net", "10.40.1.4", self.budget())
            connection.connect()
            raw.connect.assert_called_once_with(("10.40.1.4", 443))
            context.wrap_socket.assert_called_once_with(raw, server_hostname="cohorstestst.blob.core.windows.net")
            resolver.assert_not_called()
            connection.close()

    def test_blob_redirect_wrong_size_and_encoded_body_are_not_retried(self):
        for status, headers in (
            (302, {}), (403, {}), (200, {"Content-Length": "1"}),
            (200, {"Content-Length": str(len(self.archive)), "Content-Encoding": "gzip"}),
            (200, {"Content-Length": str(len(self.archive)), "Transfer-Encoding": "chunked"}),
        ):
            response = FakeResponse(self.archive, status, headers)
            connection = FakeConnection(response)
            receiver = stage.AzureReceiver(self.profile, "cohorstestst.blob.core.windows.net", self.budget())
            receiver.connection, receiver.addresses = connection, ["10.40.1.4"]
            with self.subTest(status=status, headers=headers), self.assertRaises(stage.StageError):
                receiver.download("memory-only", io.BytesIO())
            self.assertEqual(1, len(connection.requests))
            method, path, sent = connection.requests[0]
            self.assertEqual("GET", method)
            self.assertNotIn("?", path)
            self.assertEqual("Bearer memory-only", sent["Authorization"])

    def test_imds_and_blob_connection_close_responses_remain_bounded_and_no_fallback_is_used(self):
        token = self.token()
        imds = FakeConnection(FakeResponse(package.encoded(token), headers={"Content-Type": "application/json"}))
        receiver = stage.AzureReceiver(self.profile, "cohorstestst.blob.core.windows.net", self.budget())
        with patch.object(stage.http.client, "HTTPConnection", return_value=imds) as constructor:
            self.assertEqual(token["access_token"], receiver.token())
        self.assertEqual(stage.IMDS_HOST, constructor.call_args.args[0])
        self.assertEqual([("GET", stage.IMDS_PATH, {"Metadata": "true", "Accept": "application/json"})], imds.requests)
        blob = FakeConnection(FakeResponse(self.archive))
        receiver.connection, receiver.addresses = blob, ["10.40.1.4"]
        output = io.BytesIO()
        receiver.download(token["access_token"], output)
        self.assertEqual(self.archive, output.getvalue())
        self.assertEqual(1, len(blob.requests))

    def test_corrupt_or_interrupted_download_leaves_no_extracted_payload_or_temporary_archive(self):
        for corrupt in (True, False):
            def receiver_factory(profile, host, budget):
                receiver = self.fake_receiver(profile, host, budget)
                def download(token, output):
                    output.write(b"!" * len(self.archive) if corrupt else self.archive[:40])
                    return {}
                receiver.download.side_effect = download
                return receiver
            with self.subTest(corrupt=corrupt), self.assertRaises((stage.StageError, ValueError)):
                stage.stage(self.profile, self.destination, receiver_factory, clock=lambda: self.now)
            self.assertFalse(self.destination.exists())
            self.assertEqual([], list(self.root.glob(".c1-stage-*")))

    def test_overall_deadline_interrupts_a_stalled_receiver_and_restores_the_process_handler(self):
        previous = signal.getsignal(signal.SIGALRM)
        def receiver_factory(profile, host, budget):
            receiver = self.fake_receiver(profile, host, budget)
            receiver.download.side_effect = lambda *_: threading.Event().wait(5)
            return receiver
        with self.assertRaisesRegex(stage.StageError, "deadline"):
            stage.stage({**self.profile, "maxDurationSeconds": 1}, self.destination, receiver_factory, clock=lambda: self.now)
        self.assertEqual(previous, signal.getsignal(signal.SIGALRM))
        self.assertEqual((0.0, 0.0), signal.getitimer(signal.ITIMER_REAL))
        self.assertFalse(self.destination.exists())
        self.assertEqual([], list(self.root.glob(".c1-stage-*")))

    def test_clock_rollback_never_extends_the_initial_budget(self):
        tick = [0]
        clock = [self.now]
        profile = {**self.profile, "expiresAtUtc": "2030-01-01T00:00:10Z"}
        budget = stage.Budget(profile, lambda: clock[0], lambda: tick[0])
        clock[0] -= timedelta(minutes=1)
        tick[0] = 5
        self.assertEqual(5, budget.total_remaining())
        tick[0] = 11
        with self.assertRaisesRegex(stage.StageError, "deadline"):
            budget.remaining()

    def test_private_profile_rejects_shared_files_and_hardlinks(self):
        path = self.root / "profile.json"
        path.write_bytes(package.encoded(self.profile))
        path.chmod(0o644)
        with self.assertRaisesRegex(stage.StageError, "private_path"):
            stage.load_profile(path)
        path.chmod(0o600)
        self.assertEqual(self.profile, stage.load_profile(path))
        os.link(path, self.root / "profile-copy.json")
        with self.assertRaisesRegex(stage.StageError, "private_path"):
            stage.load_profile(path)

    def test_ci_refusal_precedes_profile_access_and_errors_never_print_token_text(self):
        output = io.StringIO()
        with patch.dict(os.environ, {"GITHUB_ACTIONS": "true"}), patch.object(stage, "load_profile") as load, \
                redirect_stderr(output):
            self.assertEqual(2, stage.main(["--profile", "/not-read", "--destination", "/not-created"]))
        load.assert_not_called()
        self.assertEqual("c1_stage_ci_forbidden", json.loads(output.getvalue())["code"])
        output, standard = io.StringIO(), io.StringIO()
        with patch.dict(os.environ, {"GITHUB_ACTIONS": ""}), patch.object(stage, "load_profile", return_value=self.profile), \
                patch.object(stage, "stage", side_effect=OSError("Bearer sensitive-sentinel")), \
                redirect_stderr(output), redirect_stdout(standard):
            self.assertEqual(2, stage.main(["--profile", "/not-read", "--destination", "/not-created"]))
        self.assertNotIn("sensitive-sentinel", output.getvalue())
        self.assertEqual("", standard.getvalue())


class FakeResponse(io.BytesIO):
    def __init__(self, data, status=200, headers=None):
        super().__init__(data)
        self.status = status
        self.headers = Message()
        for key, value in {"Content-Length": str(len(data)), **(headers or {})}.items():
            self.headers[key] = value

    def read1(self, size=-1):
        return self.read(size)


class FakeConnection:
    def __init__(self, response):
        self.response = response
        self.sock = Mock()
        self.requests = []

    def request(self, method, path, headers):
        self.requests.append((method, path, headers))

    def getresponse(self):
        self.sock = None
        return self.response

    def close(self):
        pass


if __name__ == "__main__":
    unittest.main()
