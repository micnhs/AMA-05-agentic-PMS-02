import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
import zipfile


sys.path.insert(0, str(Path(__file__).parents[1] / "scripts"))
import primary_package as package
from primary_control import GateError


class PrimaryPackageTests(unittest.TestCase):
    def test_hidden_flex_metadata_and_exact_triggers_are_required(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "operations.zip"
            for include_extension, names in (
                (False, package.FUNCTIONS["operations"]),
                (True, {"DeadlineWatchdog", "OperationsLive", "OldReady"}),
                (True, package.FUNCTIONS["operations"]),
            ):
                with zipfile.ZipFile(path, "w") as archive:
                    for member in ("host.json", "worker.config.json", "extensions.json", "Cohors.Operations.dll"):
                        archive.writestr(member, "non-executable unit fixture")
                    archive.writestr("functions.metadata", json.dumps([{"name": name} for name in sorted(names)]))
                    if include_extension:
                        archive.writestr(".azurefunctions/Microsoft.Azure.WebJobs.Extensions.FunctionMetadataLoader.dll",
                                         "non-executable unit fixture")
                if include_extension and names == package.FUNCTIONS["operations"]:
                    self.assertEqual(package.inspect_functions(path, "operations")["functions"], sorted(names))
                else:
                    with self.assertRaises(GateError):
                        package.inspect_functions(path, "operations")

    def test_nested_manifest_and_bytecode_are_not_unhashed_exceptions(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "release-manifest.json").write_text(json.dumps({
                "contract": "cohors-primary-release-1", "files": {},
            }), encoding="utf-8")
            for member in ("nested/release-manifest.json", "__pycache__/unbound.pyc"):
                path = root / member
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(b"unbound fixture")
                with self.subTest(member=member), self.assertRaisesRegex(GateError, "file set"):
                    package.verify_bundle(root)
                path.unlink()

    def test_unqualified_application_cannot_inherit_schema3(self):
        with self.assertRaisesRegex(GateError, "no b7/schema3 mixing"):
            package.operations_contract(Path("unused"), Path("unused"), "b7" + "0" * 38)

    def test_compiled_migration_or_operations_dll_mismatch_blocks_bundle(self):
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "source"
            bundle = Path(directory) / "bundle"
            paths = ("docs/sql-runtime-contract.md", "src/Cohors.Infrastructure/SqlOperationsStore.cs",
                     "src/Cohors.Operations/OperationsFunctions.cs", "src/Cohors.Bootstrap/Program.cs",
                     "src/Cohors.Bootstrap/BootstrapRequest.cs") + tuple(
                         "src/Cohors.Infrastructure/Migrations/" + name for name in package.OPERATIONS_MIGRATIONS)
            fixture = b"unit fixture, not a built application"
            fixture_hash = hashlib.sha256(fixture).hexdigest()
            for name in paths:
                path = source / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(fixture)
            for host in ("api", "bootstrap"):
                path = bundle / host / "Cohors.Infrastructure.dll"
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(fixture)
            (bundle / "functions").mkdir()
            with zipfile.ZipFile(bundle / "functions/cohors-operations.zip", "w") as archive:
                archive.writestr("Cohors.Infrastructure.dll", fixture)
            resource = "Cohors.Infrastructure.Migrations.003c_operations_permissions.sql"
            compiled = {"hasRestrictedPrincipalGuard": True, "assemblySha256": fixture_hash,
                        "migrationResources": {"Cohors.Infrastructure.Migrations." + name: fixture_hash
                                               for name in package.OPERATIONS_MIGRATIONS}}
            result = subprocess.CompletedProcess(args=[], returncode=0, stdout=fixture, stderr=b"")
            with patch.object(package.subprocess, "run", return_value=result), \
                    patch.object(package, "OPERATIONS_DOCUMENT_SHA256", fixture_hash):
                (bundle / "sql-contract-build.json").write_text(json.dumps(compiled), encoding="utf-8")
                contract = package.operations_contract(source, bundle, package.OPERATIONS_APPLICATION)
                self.assertFalse(contract["azurePrincipalGrantsProven"])
                self.assertFalse(contract["entraAuthenticationProven"])
                compiled["migrationResources"][resource] = "0" * 64
                (bundle / "sql-contract-build.json").write_text(json.dumps(compiled), encoding="utf-8")
                with self.assertRaisesRegex(GateError, "original application migration bytes"):
                    package.operations_contract(source, bundle, package.OPERATIONS_APPLICATION)
                compiled["migrationResources"][resource] = fixture_hash
                (bundle / "sql-contract-build.json").write_text(json.dumps(compiled), encoding="utf-8")
                with zipfile.ZipFile(bundle / "functions/cohors-operations.zip", "w") as archive:
                    archive.writestr("Cohors.Infrastructure.dll", b"another assembly")
                with self.assertRaisesRegex(GateError, "Operations ZIP uses another"):
                    package.operations_contract(source, bundle, package.OPERATIONS_APPLICATION)


if __name__ == "__main__":
    unittest.main()
