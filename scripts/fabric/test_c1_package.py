import io
from contextlib import redirect_stdout
from pathlib import Path
import stat
import tempfile
import unittest
import zipfile

import c1_package as package


class C1PackageTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="cohors-c1-package-")
        self.root = Path(self.temporary.name)
        self.source = self.root / "source"
        self.publish = self.root / "publish"
        self.output = self.root / "package"
        self.output.mkdir()
        self.commit = "a" * 40
        self.run_id = "123456789"
        for name in package.SOURCE_ASSETS:
            path = self.source / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(b"synthetic-source-asset")
        for project in package.PROJECTS:
            for lock in package.LOCK_NAMES:
                path = self.source / "src" / project / lock
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text('{"version":1,"dependencies":{}}', encoding="utf-8")
        for rid in package.RIDS:
            folder = self.publish / rid
            folder.mkdir(parents=True)
            target = ".NETCoreApp,Version=v10.0/" + rid
            runtime = {name + ".dll": {} for name in package.PROJECTS}
            deps = {"runtimeTarget": {"name": target}, "targets": {target: {"synthetic/1": {"runtime": runtime}}}}
            (folder / "Cohors.Api.deps.json").write_bytes(package.encoded(deps))
            (folder / "Cohors.Api.runtimeconfig.json").write_bytes(package.encoded({"runtimeOptions": {
                "tfm": "net10.0", "includedFrameworks": [{"name": "Microsoft.NETCore.App", "version": "10.0.0"},
                                                       {"name": "Microsoft.AspNetCore.App", "version": "10.0.0"}]}}))
            for name in runtime:
                (folder / name).write_bytes(b"synthetic-assembly-not-executed")
            (folder / ("Cohors.Api.exe" if rid == "win-x64" else "Cohors.Api")).write_bytes(b"synthetic-host-not-executed")
            (folder / "not-allowed.env").write_bytes(b"never-packaged")
            (folder / "appsettings.json").write_bytes(b"never-packaged")
            (folder / "local-results.trx").write_bytes(b"never-packaged")

    def tearDown(self):
        self.temporary.cleanup()

    def build(self):
        return package.build(self.source, self.publish, self.output, self.commit, self.run_id, "1")

    def outer(self):
        self.build()
        path = self.root / "download.zip"
        package.write_zip(path, {"package.zip": (self.output / "package.zip").read_bytes(),
                                 "archive.json": (self.output / "archive.json").read_bytes()})
        return path

    def test_roundtrip_verifies_before_extracting_and_excludes_non_allowlisted_publish_files(self):
        path = self.outer()
        destination = self.root / "verified"
        result = package.receive(path, package.digest(path.read_bytes()), self.commit, self.run_id, "1", destination)
        self.assertEqual("VerifiedNotExecuted", result["status"])
        self.assertTrue((destination / "runtimes" / "win-x64" / "Cohors.Api.exe").is_file())
        self.assertFalse(any(destination.rglob("*.env")))
        self.assertFalse(any(destination.rglob("*.trx")))
        self.assertFalse(any(destination.rglob("appsettings.json")))
        with self.assertRaisesRegex(ValueError, "new directory"):
            package.receive(path, package.digest(path.read_bytes()), self.commit, self.run_id, "1", destination)

    def test_wrong_digest_source_or_attempt_never_creates_destination(self):
        path = self.outer()
        destination = self.root / "not-created"
        for checksum, commit, attempt in (
            ("b" * 64, self.commit, "1"),
            (package.digest(path.read_bytes()), "b" * 40, "1"),
            (package.digest(path.read_bytes()), self.commit, "2"),
        ):
            with self.subTest(commit=commit, attempt=attempt):
                with self.assertRaises(ValueError):
                    package.receive(path, checksum, commit, self.run_id, attempt, destination)
                self.assertFalse(destination.exists())

    def test_credential_in_an_allowlisted_binary_refuses_upload_before_archive_creation(self):
        path = self.publish / "linux-x64" / "Cohors.Api.dll"
        path.write_bytes(("prefix ghp_" + "Z" * 36 + " suffix").encode("utf-16-le"))
        with self.assertRaisesRegex(ValueError, "Credential-shaped"):
            self.build()
        self.assertFalse((self.output / "package.zip").exists())

    def test_wrong_rid_and_framework_dependent_publish_are_not_release_payloads(self):
        path = self.publish / "win-x64" / "Cohors.Api.runtimeconfig.json"
        original = path.read_bytes()
        path.write_bytes(package.encoded({"runtimeOptions": {"tfm": "net10.0",
                                                             "framework": {"name": "Microsoft.NETCore.App"}}}))
        with self.assertRaisesRegex(ValueError, "self-contained"):
            self.build()
        path.write_bytes(original)
        path = self.publish / "win-x64" / "Cohors.Api.deps.json"
        deps = package.object_json(path.read_bytes())
        deps["runtimeTarget"]["name"] = ".NETCoreApp,Version=v10.0/linux-x64"
        path.write_bytes(package.encoded(deps))
        with self.assertRaisesRegex(ValueError, "runtime target"):
            self.build()
        self.assertFalse((self.output / "package.zip").exists())

    def test_unknown_payload_file_remains_forbidden_even_with_consistent_attacker_hashes(self):
        receipt = self.build()
        original = (self.output / "package.zip").read_bytes()
        payload = package.read_zip(original)
        manifest = package.object_json(payload["manifest.json"])
        injected = b"unexpected-private-state"
        payload["state.json"] = injected
        manifest["files"].append({"path": "state.json", "length": len(injected), "sha256": package.digest(injected)})
        payload["manifest.json"] = package.encoded(manifest)
        changed = self.root / "changed.zip"
        package.write_zip(changed, payload)
        data = changed.read_bytes()
        receipt.update(archiveSha256=package.digest(data), archiveLength=len(data),
                       manifestSha256=package.digest(payload["manifest.json"]))
        with self.assertRaisesRegex(ValueError, "allowlist"):
            package.verify(data, receipt, self.commit, self.run_id, "1")

    def test_zip_paths_duplicates_and_symlinks_are_rejected_without_writing(self):
        for name, mode, duplicate in (
            ("../outside", stat.S_IFREG, False),
            ("C:/outside", stat.S_IFREG, False),
            ("folder\\outside", stat.S_IFREG, False),
            ("safe", stat.S_IFLNK, False),
            ("safe", stat.S_IFREG, True),
        ):
            with self.subTest(name=name, mode=mode):
                stream = io.BytesIO()
                with zipfile.ZipFile(stream, "w") as archive:
                    info = zipfile.ZipInfo(name)
                    info.external_attr = (mode | 0o644) << 16
                    archive.writestr(info, "synthetic")
                    if duplicate:
                        archive.writestr("SAFE", "synthetic")
                with self.assertRaises(ValueError):
                    package.read_zip(stream.getvalue())

    def test_missing_dependency_and_malformed_or_duplicate_metadata_cannot_pass(self):
        (self.publish / "linux-x64" / "Cohors.Domain.dll").unlink()
        with self.assertRaisesRegex(ValueError, "Missing or ambiguous"):
            self.build()
        with self.assertRaisesRegex(ValueError, "Duplicate"):
            package.object_json(b'{"contract":"x","contract":"y"}')
        with self.assertRaises(ValueError):
            package.relative_name("folder/CON.txt")

    def test_source_links_and_destination_links_are_refused(self):
        source = self.publish / "linux-x64" / "Cohors.Domain.dll"
        actual = self.root / "actual.dll"
        source.rename(actual)
        source.symlink_to(actual)
        with self.assertRaisesRegex(ValueError, "Symlink/reparse"):
            self.build()
        source.unlink()
        actual.rename(source)
        archive = self.outer()
        linked_parent = self.root / "linked-parent"
        linked_parent.symlink_to(self.root, target_is_directory=True)
        with self.assertRaisesRegex(ValueError, "Symlink/reparse"):
            package.receive(archive, package.digest(archive.read_bytes()), self.commit, self.run_id, "1",
                            linked_parent / "refused")
        self.assertFalse((self.root / "refused").exists())

    def test_branch_sdk_and_unknown_manifest_metadata_cannot_be_substituted(self):
        self.build()
        payload = package.read_zip((self.output / "package.zip").read_bytes())
        manifest = package.object_json(payload["manifest.json"])
        for field, value in (("branch", "main"), ("sdkVersion", "10.0.999"), ("unexpected", "private-start-data")):
            with self.subTest(field=field):
                with self.assertRaises(ValueError):
                    package.validate_provenance(dict(manifest, **{field: value}), self.commit, self.run_id, "1")

    def test_explicit_rid_lock_preparation_cannot_upgrade_the_base_graph(self):
        baseline = {"version": 1, "dependencies": {"net10.0": {
            "Example": {"type": "Transitive", "resolved": "1.2.3", "contentHash": "fixed-content-hash"}}}}
        candidate = {"version": 1, "dependencies": {
            **baseline["dependencies"], "net10.0/win-x64": {}, "net10.0/linux-x64": {}}}
        for project in package.PROJECTS:
            folder = self.source / "src" / project
            (folder / "packages.lock.json").write_bytes(package.encoded(baseline))
            (folder / "packages.c1.lock.json").write_bytes(package.encoded(candidate))
        with redirect_stdout(io.StringIO()):
            result = package.runtime_lock_candidates(self.source, self.commit, self.run_id, "1")
        self.assertEqual("CandidateSourceLocksOnly", result["status"])
        self.assertFalse(result["packageCreated"])
        changed = package.object_json(package.encoded(candidate))
        changed["dependencies"]["net10.0"]["Example"]["resolved"] = "9.0.0"
        (self.source / "src" / package.PROJECTS[-1] / "packages.c1.lock.json").write_bytes(package.encoded(changed))
        output = io.StringIO()
        with redirect_stdout(output), self.assertRaisesRegex(ValueError, "changed pinned base"):
            package.runtime_lock_candidates(self.source, self.commit, self.run_id, "1")
        self.assertEqual("", output.getvalue())


if __name__ == "__main__":
    unittest.main()
