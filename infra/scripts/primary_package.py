"""Bind real hosted build outputs; never manufacture image digests or trigger metadata."""

import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import stat
import subprocess
import zipfile

from primary_control import GateError, file_hash, read_json, timestamp, write_json


FUNCTIONS = {
    "workflows": {"DispatchOutbox", "CohorsOutboxV1", "ProcessOutbox", "WorkflowLive", "WorkflowReady"},
    "operations": {"DeadlineWatchdog", "OperationsLive", "OperationsReady"},
}
OPERATIONS_APPLICATION = "dd112705af2baf6c4e7787794b782f4f8937b1e8"
OPERATIONS_DOCUMENT_SHA256 = "7baed39d5ca502c44aa7b1f3c839350cfa37595dfd52b889a359c767e8613e3b"
OPERATIONS_MIGRATIONS = (
    "001_initial.sql", "002_outbox_delivery.sql", "003a_operations_quote.sql",
    "003b_operations_write.sql", "003c_operations_permissions.sql",
)


def safe_member(info):
    path = PurePosixPath(info.filename)
    if path.is_absolute() or ".." in path.parts or "\\" in info.filename or ":" in info.filename:
        raise GateError("Archive contains an unsafe path.")
    if stat.S_ISLNK(info.external_attr >> 16):
        raise GateError("Archive contains a symlink.")
    return path


def inspect_functions(path, host):
    with zipfile.ZipFile(path) as archive:
        names = {str(safe_member(info)) for info in archive.infolist()}
        required = {"host.json", "functions.metadata", "worker.config.json", "extensions.json",
                    ".azurefunctions/Microsoft.Azure.WebJobs.Extensions.FunctionMetadataLoader.dll",
                    f"Cohors.{host.title()}.dll"}
        if not required.issubset(names):
            raise GateError("Flex ZIP is missing real package-root/hidden-extension files.")
        metadata = json.loads(archive.read("functions.metadata"))
        actual = {item["name"] for item in metadata}
        if actual != FUNCTIONS[host]:
            raise GateError("Flex trigger names differ from the bound primary application contract.")
        return {"sha256": file_hash(path), "functions": sorted(actual),
                "metadataSha256": hashlib.sha256(archive.read("functions.metadata")).hexdigest()}


def verify_bundle(root, expected_app=None, expected_infra=None):
    root = Path(root)
    manifest = read_json(root / "release-manifest.json")
    if manifest.get("contract") != "cohors-primary-release-1":
        raise GateError("Unsupported release package.")
    if expected_app and manifest["applicationCommit"] != expected_app:
        raise GateError("Package application source is not the selected immutable candidate.")
    if expected_infra and manifest["infrastructureCommit"] != expected_infra:
        raise GateError("Package infrastructure source changed.")
    members = [path for path in root.rglob("*") if path.is_file()]
    if any(path.is_symlink() or not path.resolve().is_relative_to(root.resolve()) for path in members):
        raise GateError("A package member escapes its hash-bound root.")
    actual = {path.relative_to(root).as_posix() for path in members
              if path != root / "release-manifest.json"}
    if actual != set(manifest["files"]):
        raise GateError("Package file set differs from its manifest.")
    for name, expected in manifest["files"].items():
        if file_hash(root / name) != expected:
            raise GateError("Package member hash mismatch.")
    for host in FUNCTIONS:
        if inspect_functions(root / f"functions/cohors-{host}.zip", host) != manifest["functions"][host]:
            raise GateError("Functions artifact or generated metadata changed.")
    contract = manifest.get("operationsSqlContract")
    if (manifest["applicationCommit"] != OPERATIONS_APPLICATION or not isinstance(contract, dict)
            or contract.get("contract") != "cohors-operations-write-v1" or contract.get("schemaVersion") != 3
            or contract.get("sourceFiles", {}).get("docs/sql-runtime-contract.md") != OPERATIONS_DOCUMENT_SHA256):
        raise GateError("No built restricted Operations source adapter is registered for this package.")
    return manifest


def operations_contract(application_root, bundle, app):
    if app != OPERATIONS_APPLICATION:
        raise GateError("The chosen source has no reviewed restricted SQL adapter; no b7/schema3 mixing.")
    source = Path(application_root)
    paths = ["docs/sql-runtime-contract.md", "src/Cohors.Infrastructure/SqlStateStore.cs",
             "src/Cohors.Infrastructure/SqlOperationsStore.cs",
             "src/Cohors.Operations/OperationsFunctions.cs", "src/Cohors.Bootstrap/Program.cs",
             "src/Cohors.Bootstrap/BootstrapRequest.cs"]
    migrations = sorted((source / "src/Cohors.Infrastructure/Migrations").glob("*.sql"))
    if tuple(path.name for path in migrations) != OPERATIONS_MIGRATIONS:
        raise GateError("The complete original schema1/2/3 migration file set is required.")
    paths += [path.relative_to(source).as_posix() for path in migrations]
    bound = {}
    for path in paths:
        result = subprocess.run(["git", "-C", str(source), "show", app + ":" + path],
                                capture_output=True, timeout=30, check=False)
        expected = hashlib.sha256(result.stdout).hexdigest()
        if result.returncode or expected != file_hash(source / path):
            raise GateError("Application contract source changed after its immutable checkout.")
        bound[path] = expected
    if bound["docs/sql-runtime-contract.md"] != OPERATIONS_DOCUMENT_SHA256:
        raise GateError("The original restricted SQL source document changed.")
    compiled = read_json(bundle / "sql-contract-build.json")
    if compiled.get("hasRestrictedPrincipalGuard") is not True or compiled.get("schemaVersion") != 3:
        raise GateError("Compiled application lacks the original restricted-principal guard.")
    resources = compiled["migrationResources"]
    if len(resources) != len(migrations) or any(
            [value for name, value in resources.items() if name.endswith("." + path.name)]
            != [bound[path.relative_to(source).as_posix()]] for path in migrations):
        raise GateError("Compiled embedded migrations are not the exact original application migration bytes.")
    modules = {}
    for filename, name in (("003a_operations_quote.sql", "dbo.CohorsOperationsQuote"),
                           ("003b_operations_write.sql", "dbo.CohorsOperationsWrite")):
        raw = (source / "src/Cohors.Infrastructure/Migrations" / filename).read_bytes().decode("utf-8-sig")
        modules[name] = hashlib.sha256(raw.encode("utf-16le")).hexdigest()
    if compiled.get("moduleDefinitionSha256") != modules:
        raise GateError("Compiled SQL module-definition hashes differ from the original migration text.")
    for host in ("api", "bootstrap"):
        if file_hash(bundle / host / "Cohors.Infrastructure.dll") != compiled["assemblySha256"]:
            raise GateError("Private tool/API/Bootstrap packages use different infrastructure assemblies.")
    with zipfile.ZipFile(bundle / "functions/cohors-operations.zip") as archive:
        if hashlib.sha256(archive.read("Cohors.Infrastructure.dll")).hexdigest() != compiled["assemblySha256"]:
            raise GateError("The real Operations ZIP uses another application/schema assembly.")
    return {"contract": "cohors-operations-write-v1", "schemaVersion": 3,
            "role": "cohors_operations_runtime", "procedure": "dbo.CohorsOperationsWrite",
            "applicationCommit": app, "sourceFiles": bound, "compiledAssemblySha256": compiled["assemblySha256"],
            "moduleDefinitionSha256": modules,
            "entraAuthenticationProven": False, "azurePrincipalGrantsProven": False}


def build_manifest(root, app, infra, run_id, application_root):
    if not all(re.fullmatch(r"[0-9a-f]{40}", value) for value in (app, infra)):
        raise GateError("Both source commits must be full immutable Git SHAs.")
    required = ("api/Cohors.Api", "agents/Cohors.AgentHost", "bootstrap/Cohors.Bootstrap",
                "images/api.oci.tar", "images/agents.oci.tar", "tools/sql/Cohors.InfraSql",
                "infra/bootstrap/nva_proxy.py", "infra/scripts/primary_host.py",
                "tools/dotnet/dotnet", "tools/dotnet/runtime-binding.json", "sql-contract-build.json")
    if any(not (root / name).is_file() for name in required):
        raise GateError("Hosted build output is incomplete; no synthetic placeholders are accepted.")
    files = sorted(path for path in root.rglob("*") if path.is_file())
    if any(path.is_symlink() or path.name in (".env", "settings.json") and ".local" in path.parts
           for path in files):
        raise GateError("Unacceptable link or local settings file in release.")
    value = {"contract": "cohors-primary-release-1", "applicationCommit": app, "infrastructureCommit": infra,
             "githubRunId": int(run_id), "builtAtUtc": timestamp(), "cloudReleased": False,
             "files": {path.relative_to(root).as_posix(): file_hash(path) for path in files},
             "functions": {host: inspect_functions(root / f"functions/cohors-{host}.zip", host)
                           for host in FUNCTIONS},
             "images": {}, "operationsSqlContract": operations_contract(application_root, root, app)}
    for host in ("api", "agents"):
        image_digest = (root / f"images/{host}.digest").read_text(encoding="utf-8").strip()
        if not re.fullmatch(r"sha256:[0-9a-f]{64}", image_digest):
            raise GateError("A real OCI manifest digest is required.")
        value["images"][host] = image_digest
    write_json(root / "release-manifest.json", value)
    verify_bundle(root, app, infra)
    return value


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--application-commit", required=True)
    parser.add_argument("--application-root", type=Path, required=True)
    parser.add_argument("--infrastructure-commit", required=True)
    parser.add_argument("--run-id", required=True)
    parser.add_argument("--zip", type=Path, required=True)
    args = parser.parse_args()
    build_manifest(args.root, args.application_commit, args.infrastructure_commit, args.run_id, args.application_root)
    if args.zip.exists():
        raise GateError("Refusing to overwrite an existing bound bundle.")
    args.zip.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(args.zip, "x", compression=zipfile.ZIP_DEFLATED) as archive:
        for path in sorted(args.root.rglob("*")):
            if path.is_file():
                archive.write(path, path.relative_to(args.root).as_posix())
    print(json.dumps({"bundleSha256": file_hash(args.zip),
                      "releaseManifestSha256": file_hash(args.root / "release-manifest.json"),
                      "cloudReleased": False}))


if __name__ == "__main__":
    main()
