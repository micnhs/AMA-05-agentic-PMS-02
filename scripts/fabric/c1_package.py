"""Bounded C1 deployment archives. Verification never executes their contents."""

import argparse
import base64
import hashlib
import io
import json
import os
from pathlib import Path, PurePosixPath
import re
import stat
import sys
import zipfile


LEGACY_CONTRACT = "cohors-fabric-c1-package-1"
WORKER_CONTRACT = "cohors-fabric-c1-package-2"
CONTRACT = "cohors-fabric-c1-package-3"
CONTRACTS = (LEGACY_CONTRACT, WORKER_CONTRACT, CONTRACT)
REPOSITORY = "micnhs/AMA-05-agentic-PMS-02"
BRANCH = "micnhs-cohors-fabric-umsetzung"
WORKFLOW = ".github/workflows/cohors-fabric-contracts.yml"
SDK = "10.0.404"
MAX_ARCHIVE = 256 * 1024 * 1024
MAX_EXPANDED = 1024 * 1024 * 1024
MAX_FILES = 2048
RIDS = ("win-x64", "linux-x64")
PROJECTS = ("Cohors.Api", "Cohors.Agents", "Cohors.Infrastructure", "Cohors.Application", "Cohors.Domain")
LOCK_NAMES = ("packages.lock.json", "packages.c1.lock.json")
LEGACY_SOURCE_ASSETS = (
    "infra/fabric/c1.foundation.json",
    "infra/fabric/c1.workspace-network.json",
    "fabric/c1/deployment.template.json",
    "fabric/c1/inputs.template.json",
    "fabric/c1/execution.template.json",
    "fabric/c1/synthetic-source.json",
    "src/Cohors.Api/ReportFonts/LICENSE",
    "src/Cohors.Api/ReportFonts/PDFsharp.LICENSE",
    "scripts/fabric/c1_package.py",
)
WORKER_SOURCE_ASSETS = (*LEGACY_SOURCE_ASSETS, "scripts/fabric/c1_private_stage.py")
SOURCE_ASSETS = (*WORKER_SOURCE_ASSETS, "infra/fabric/c1.network-access.json")
FORBIDDEN_SUFFIXES = {".pfx", ".p12", ".pem", ".key", ".log", ".trx", ".pdb"}
SECRET_PATTERNS = (
    re.compile(rb"-----BEGIN (?:RSA |EC |ENCRYPTED )?PRIVATE KEY-----\s+[A-Za-z0-9+/=\r\n]{64,}-----END "),
    re.compile(rb"\beyJ[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{16,}\b"),
    re.compile(rb"\b(?:ghp_|github_pat_)[A-Za-z0-9_]{30,}\b"),
    re.compile(rb"\bAccountKey\s*=\s*[A-Za-z0-9+/=]{40,}", re.IGNORECASE),
    re.compile(rb"(?:[?&]|&amp;)sig=[A-Za-z0-9%+/=_-]{30,}", re.IGNORECASE),
    re.compile(rb"""["']?(?:client_secret|clientSecret|password)["']?\s*[:=]\s*["'][^"' \r\n]{16,}["']""", re.IGNORECASE),
)


def require(condition, message):
    if not condition:
        raise ValueError(message)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def encoded(value):
    return (json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=True) + "\n").encode()


def source_assets(contract):
    require(contract in CONTRACTS, "Unsupported package contract")
    return {LEGACY_CONTRACT: LEGACY_SOURCE_ASSETS, WORKER_CONTRACT: WORKER_SOURCE_ASSETS,
            CONTRACT: SOURCE_ASSETS}[contract]


def object_json(data):
    def unique(pairs):
        result = {}
        for key, value in pairs:
            require(key not in result, "Duplicate JSON property")
            result[key] = value
        return result
    result = json.loads(data, object_pairs_hook=unique)
    require(isinstance(result, dict), "Expected JSON object")
    return result


def relative_name(name):
    require(isinstance(name, str) and 0 < len(name) <= 300, "Invalid archive path")
    require(re.fullmatch(r"[A-Za-z0-9_.\-/]+", name) is not None, "Nonportable archive path")
    path = PurePosixPath(name)
    require(not path.is_absolute() and str(path) == name and not name.endswith("/"), "Noncanonical archive path")
    for part in path.parts:
        require(part not in {".", ".."} and not part.endswith((".", " ")), "Unsafe archive path segment")
        require(part.lower().split(".")[0] not in {"con", "prn", "aux", "nul", *(f"com{i}" for i in range(10)),
                                                 *(f"lpt{i}" for i in range(10))}, "Reserved Windows path")
        require(not part.lower().startswith((".env", ".local")), "Private configuration path")
    require(path.suffix.lower() not in FORBIDDEN_SUFFIXES, "Forbidden deployment payload type")
    return name


def no_links(path):
    path = Path(path).absolute()
    for candidate in (path, *path.parents):
        if not candidate.exists() and not candidate.is_symlink():
            continue
        attributes = candidate.lstat()
        require(not stat.S_ISLNK(attributes.st_mode) and
                not getattr(attributes, "st_file_attributes", 0) & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0),
                "Symlink/reparse path is not a deployment input or destination")
    return path


def inspect_content(name, data):
    relative_name(name)
    # UTF-16 strings in managed binaries also need inspection; headers alone
    # are not private keys (cryptographic libraries contain PEM label strings).
    for candidate in (data, data.replace(b"\x00", b"")):
        require(not any(pattern.search(candidate) for pattern in SECRET_PATTERNS),
                f"Credential-shaped content refused: {name}")


def runtime_allowlist(names, read_file, rid):
    deps = object_json(read_file("Cohors.Api.deps.json"))
    target = deps.get("runtimeTarget", {}).get("name", "")
    require(target.endswith("/" + rid), "Publish runtime target differs")
    require(target in deps.get("targets", {}), "Publish dependency target is absent")
    config = object_json(read_file("Cohors.Api.runtimeconfig.json")).get("runtimeOptions", {})
    require(config.get("tfm") == "net10.0" and "framework" not in config and "frameworks" not in config and
            {item.get("name") for item in config.get("includedFrameworks", [])} ==
            {"Microsoft.NETCore.App", "Microsoft.AspNetCore.App"}, "Only self-contained .NET 10 C1 publishes are allowed")
    allowed = {"Cohors.Api.deps.json", "Cohors.Api.runtimeconfig.json",
               "Cohors.Api.exe" if rid == "win-x64" else "Cohors.Api"}
    for library in deps["targets"][target].values():
        for section in ("runtime", "native", "resources", "runtimeTargets"):
            for asset, details in library.get(section, {}).items():
                relative_name(asset)
                leaf = PurePosixPath(asset).name
                candidates = {asset, leaf}
                if section == "resources":
                    locale = details.get("locale")
                    require(isinstance(locale, str) and re.fullmatch(r"[A-Za-z0-9-]+", locale), "Unbound resource locale")
                    candidates = {locale + "/" + leaf}
                present = candidates & names
                if not present and section == "runtimeTargets":
                    continue
                require(len(present) == 1, "Missing or ambiguous publish dependency: " + asset)
                allowed.update(present)
    require(all(project + ".dll" in allowed for project in PROJECTS), "Missing C1 project assembly")
    for optional_license in ("LICENSE.txt", "LICENSE.TXT", "ThirdPartyNotices.txt", "THIRD-PARTY-NOTICES.TXT"):
        if optional_license in names:
            allowed.add(optional_license)
    return allowed


def runtime_files(folder, rid):
    folder = no_links(folder)
    files = {path.relative_to(folder).as_posix(): path for path in folder.rglob("*") if path.is_file()}
    def read_file(name):
        require(name in files, "Missing runtime configuration")
        return no_links(files[name]).read_bytes()
    allowed = runtime_allowlist(set(files), read_file, rid)
    result = {}
    for name in sorted(allowed):
        path = no_links(folder / relative_name(name))
        require(path.is_file() and path.stat().st_size <= MAX_EXPANDED, "Invalid publish file")
        result["runtimes/" + rid + "/" + name] = path.read_bytes()
    return result


def validate_provenance(manifest, commit, run_id, attempt):
    require(set(manifest) == {"contract", "repository", "branch", "workflow", "sdkVersion", "sourceCommit", "runId",
                             "runAttempt", "request", "sourceLocks", "evidenceLevel", "files"} and
            manifest.get("evidenceLevel") == "source-build-not-native-windows-or-cloud-evidence", "Unexpected package metadata")
    require(manifest.get("contract") in CONTRACTS and manifest.get("repository") == REPOSITORY and
            manifest.get("branch") == BRANCH and manifest.get("workflow") == WORKFLOW and
            manifest.get("sdkVersion") == SDK and manifest.get("sourceCommit") == commit and
            manifest.get("runId") == str(run_id) and manifest.get("runAttempt") == str(attempt) and
            manifest.get("request") == "commit-trailer:Cohors-C1-Package: requested" and
            re.fullmatch(r"[0-9a-f]{40}", commit) is not None and
            re.fullmatch(r"[1-9][0-9]*", str(run_id)) is not None and
            re.fullmatch(r"[1-9][0-9]*", str(attempt)) is not None, "Package source/run/SDK binding differs")
    locks = manifest.get("sourceLocks")
    require(isinstance(locks, list) and len(locks) == len(PROJECTS) * len(LOCK_NAMES), "Source lockfile binding is absent or unexpected")
    names = set()
    for item in locks:
        require(isinstance(item, dict) and set(item) == {"path", "sha256", "length"}, "Invalid lockfile metadata")
        name = relative_name(item["path"])
        require(name not in names and re.fullmatch(r"src/Cohors\.[A-Za-z]+/packages(?:\.c1)?\.lock\.json", name) and
                re.fullmatch(r"[0-9a-f]{64}", item["sha256"]) and
                type(item["length"]) is int and item["length"] > 0, "Invalid source lockfile binding")
        names.add(name)
    require(names == {"src/" + project + "/" + lock for project in PROJECTS for lock in LOCK_NAMES}, "Incomplete C1 lockfile set")


def write_zip(path, payload):
    require(not path.exists(), "Deployment archive already exists")
    with zipfile.ZipFile(path, "x", zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for name, data in sorted(payload.items()):
            info = zipfile.ZipInfo(relative_name(name), (1980, 1, 1, 0, 0, 0))
            executable = name == "runtimes/linux-x64/Cohors.Api" or name == "runtimes/linux-x64/createdump"
            info.external_attr = (stat.S_IFREG | (0o755 if executable else 0o644)) << 16
            info.create_system = 3
            info.compress_type = zipfile.ZIP_DEFLATED
            archive.writestr(info, data, compress_type=zipfile.ZIP_DEFLATED, compresslevel=9)


def build(root, publish_root, destination, commit, run_id, attempt, verification_directory=None):
    root, publish_root, destination = no_links(root), no_links(publish_root), no_links(destination)
    require(destination.is_dir() and not any(destination.iterdir()), "Package output directory must be empty")
    payload = {}
    for rid in RIDS:
        payload.update(runtime_files(publish_root / rid, rid))
    for name in SOURCE_ASSETS:
        path = no_links(root / name)
        require(path.is_file(), "Missing explicitly allowed source asset: " + name)
        payload["assets/" + name] = path.read_bytes()
    locks = []
    for project in PROJECTS:
        for lock in LOCK_NAMES:
            name = "src/" + project + "/" + lock
            data = no_links(root / name).read_bytes()
            object_json(data)
            locks.append({"path": name, "length": len(data), "sha256": digest(data)})
    require(len(payload) <= MAX_FILES and sum(map(len, payload.values())) <= MAX_EXPANDED, "Expanded package limit exceeded")
    for name, data in payload.items():
        inspect_content(name, data)
    manifest = {
        "contract": CONTRACT, "repository": REPOSITORY, "branch": BRANCH, "workflow": WORKFLOW,
        "sourceCommit": commit, "runId": str(run_id), "runAttempt": str(attempt), "sdkVersion": SDK,
        "request": "commit-trailer:Cohors-C1-Package: requested", "sourceLocks": locks,
        "evidenceLevel": "source-build-not-native-windows-or-cloud-evidence",
        "files": [{"path": name, "length": len(data), "sha256": digest(data)} for name, data in sorted(payload.items())],
    }
    validate_provenance(manifest, commit, run_id, attempt)
    manifest_bytes = encoded(manifest)
    payload["manifest.json"] = manifest_bytes
    archive_path = destination / "package.zip"
    write_zip(archive_path, payload)
    archive_bytes = archive_path.read_bytes()
    require(len(archive_bytes) + 65536 <= MAX_ARCHIVE, "Compressed package exceeds the 256 MiB upload envelope")
    receipt = {"contract": CONTRACT, "sourceCommit": commit, "runId": str(run_id), "runAttempt": str(attempt),
               "archiveSha256": digest(archive_bytes), "archiveLength": len(archive_bytes),
               "manifestSha256": digest(manifest_bytes)}
    verified_manifest, verified_payload = verify(archive_bytes, receipt, commit, run_id, attempt)
    (destination / "archive.json").write_bytes(encoded(receipt))
    if verification_directory is not None:
        extract(verified_manifest, verified_payload, receipt, verification_directory)
    return receipt


def read_zip(data, names=None):
    require(len(data) <= MAX_ARCHIVE, "Archive size limit exceeded")
    result, seen = {}, set()
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        entries = archive.infolist()
        require(0 < len(entries) <= MAX_FILES + 1 and
                sum(entry.file_size for entry in entries) <= MAX_EXPANDED, "Expanded archive limit exceeded")
        for entry in entries:
            name = relative_name(entry.filename)
            require(name.casefold() not in seen and not entry.is_dir() and not entry.flag_bits & 1 and
                    entry.compress_type in (zipfile.ZIP_STORED, zipfile.ZIP_DEFLATED) and
                    stat.S_IFMT(entry.external_attr >> 16) in (0, stat.S_IFREG) and
                    not (entry.external_attr >> 16) & 0o7000, "Duplicate, encrypted or special archive entry")
            if names is not None:
                require(name in names, "Unexpected outer artifact file")
            seen.add(name.casefold())
            result[name] = archive.read(entry)
    return result


def verify(data, receipt, commit, run_id, attempt):
    require(set(receipt) == {"contract", "sourceCommit", "runId", "runAttempt", "archiveSha256", "archiveLength", "manifestSha256"} and
            receipt.get("contract") in CONTRACTS and receipt.get("sourceCommit") == commit and
            receipt.get("runId") == str(run_id) and receipt.get("runAttempt") == str(attempt) and
            receipt.get("archiveLength") == len(data) and receipt.get("archiveSha256") == digest(data), "Archive digest/run binding differs")
    payload = read_zip(data)
    require("manifest.json" in payload and len(payload["manifest.json"]) <= 2 * 1024 * 1024 and
            digest(payload["manifest.json"]) == receipt.get("manifestSha256"),
            "Manifest digest differs")
    manifest = object_json(payload.pop("manifest.json"))
    validate_provenance(manifest, commit, run_id, attempt)
    require(manifest["contract"] == receipt["contract"], "Manifest/archive contract differs")
    records = manifest.get("files")
    require(isinstance(records, list) and len(records) == len(payload), "Manifest file inventory differs")
    expected = {}
    for record in records:
        require(isinstance(record, dict) and set(record) == {"path", "length", "sha256"}, "Invalid file manifest")
        name = relative_name(record["path"])
        require(name not in expected and type(record["length"]) is int and record["length"] >= 0 and
                re.fullmatch(r"[0-9a-f]{64}", record["sha256"]), "Invalid file hash/length")
        expected[name] = record
    require(set(expected) == set(payload), "Unknown or missing package file")
    asset_names = {"assets/" + name for name in source_assets(manifest["contract"])}
    require(asset_names.issubset(payload), "Required source asset is absent")
    runtime_names = set()
    for rid in RIDS:
        prefix = "runtimes/" + rid + "/"
        # Reconstruct the dependency allowlist without materializing untrusted files.
        runtime = {name[len(prefix):]: content for name, content in payload.items() if name.startswith(prefix)}
        allowed = runtime_allowlist(set(runtime), lambda name: runtime.get(name, b"{}"), rid)
        require(set(runtime) == allowed and all(project + ".dll" in runtime for project in PROJECTS),
                "Unknown or missing runtime file")
        runtime_names.update(prefix + name for name in runtime)
    require(set(payload) == asset_names | runtime_names, "File is outside the deployment allowlist")
    for name, content in payload.items():
        require(len(content) == expected[name]["length"] and digest(content) == expected[name]["sha256"], "File hash/length differs")
        inspect_content(name, content)
    return manifest, payload


def extract(manifest, payload, receipt, destination):
    require(Path(destination).is_absolute(), "Extraction destination must be absolute")
    target = no_links(destination)
    require(target.parent.is_dir() and not target.exists(), "Extraction requires a new directory under an existing non-link parent")
    target.mkdir(mode=0o700)
    for name, content in payload.items():
        output = target.joinpath(*PurePosixPath(name).parts)
        output.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
        no_links(output)
        with output.open("xb") as file:
            file.write(content)
        if os.name != "nt":
            output.chmod(0o700 if name in {"runtimes/linux-x64/Cohors.Api", "runtimes/linux-x64/createdump"} else 0o600)
    (target / "manifest.json").write_bytes(encoded(manifest))
    (target / "archive.json").write_bytes(encoded(receipt))


def verify_download(archive, outer_sha256, commit, run_id, attempt):
    archive = no_links(archive)
    require(archive.is_file() and archive.stat().st_size <= MAX_ARCHIVE, "Archive size limit exceeded")
    with archive.open("rb") as file:
        data = file.read(MAX_ARCHIVE + 1)
    require(len(data) <= MAX_ARCHIVE, "Archive size limit exceeded")
    require(re.fullmatch(r"[0-9a-f]{64}", outer_sha256) and digest(data) == outer_sha256, "GitHub artifact archive digest differs")
    outer = read_zip(data, {"package.zip", "archive.json"})
    require(set(outer) == {"package.zip", "archive.json"}, "Incomplete GitHub artifact")
    require(len(outer["archive.json"]) <= 4096, "Oversized archive receipt")
    receipt = object_json(outer["archive.json"])
    manifest, payload = verify(outer["package.zip"], receipt, commit, run_id, attempt)
    return manifest, payload, receipt, data


def receive(archive, outer_sha256, commit, run_id, attempt, destination=None, retain_archive=False):
    require(not retain_archive or destination is not None, "Retaining the archive requires an extraction destination")
    manifest, payload, receipt, data = verify_download(archive, outer_sha256, commit, run_id, attempt)
    if destination is not None:
        extract(manifest, payload, receipt, destination)
        if retain_archive:
            retained = Path(destination) / "transport.zip"
            with retained.open("xb") as file:
                file.write(data)
            if os.name != "nt":
                retained.chmod(0o600)
    return {"status": "VerifiedNotExecuted", "sourceCommit": commit, "runId": str(run_id), "runAttempt": str(attempt),
            "archiveSha256": receipt["archiveSha256"], "files": len(payload)}


def runtime_lock_candidates(root, commit, run_id, attempt):
    root = no_links(root)
    records = []
    for project in PROJECTS:
        folder = root / "src" / project
        original = object_json(no_links(folder / "packages.lock.json").read_bytes())
        data = no_links(folder / "packages.c1.lock.json").read_bytes()
        require(len(data) <= 256 * 1024, "Oversized runtime lockfile")
        candidate = object_json(data)
        require(candidate.get("version") == original.get("version") == 1, "Unsupported runtime lockfile format")
        before, after = original["dependencies"], candidate["dependencies"]
        require(set(before) == {"net10.0"} and set(after) == {"net10.0", "net10.0/win-x64", "net10.0/linux-x64"},
                "Unexpected runtime lock target set")
        if before["net10.0"] != after["net10.0"]:
            changes = []
            for name in sorted(set(before["net10.0"]) | set(after["net10.0"])):
                old, new = before["net10.0"].get(name), after["net10.0"].get(name)
                if old != new:
                    changes.append({"package": name, "before": old, "after": new})
            raise ValueError("RID preparation changed pinned base packages or hashes: " + project + " " +
                             json.dumps(changes, separators=(",", ":")))
        inspect_content("packages.c1.lock.json", data)
        records.append({"path": "src/" + project + "/packages.c1.lock.json", "length": len(data),
                        "sha256": digest(data), "contentBase64": base64.b64encode(data).decode("ascii")})
    for record in records:
        content = record.pop("contentBase64")
        chunks = [content[index:index + 4096] for index in range(0, len(content), 4096)]
        for index, chunk in enumerate(chunks):
            print("C1_RUNTIME_LOCK " + json.dumps({"sourceCommit": commit, "runId": str(run_id), "runAttempt": str(attempt),
                **record, "chunk": index, "chunkCount": len(chunks), "contentBase64": chunk}, separators=(",", ":")))
    return {"status": "CandidateSourceLocksOnly", "files": len(records), "packageCreated": False}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    create = commands.add_parser("build")
    create.add_argument("--root", required=True)
    create.add_argument("--publish-root", required=True)
    create.add_argument("--destination", required=True)
    create.add_argument("--verification-directory")
    get = commands.add_parser("receive")
    get.add_argument("--archive", required=True)
    get.add_argument("--outer-sha256", required=True)
    get.add_argument("--destination")
    get.add_argument("--retain-archive", action="store_true")
    locks = commands.add_parser("locks")
    locks.add_argument("--root", required=True)
    for command in (create, get, locks):
        command.add_argument("--source-commit", required=True)
        command.add_argument("--run-id", required=True)
        command.add_argument("--run-attempt", required=True)
    args = parser.parse_args()
    try:
        if args.command == "build":
            result = build(args.root, args.publish_root, args.destination, args.source_commit, args.run_id, args.run_attempt, args.verification_directory)
        elif args.command == "receive":
            result = receive(args.archive, args.outer_sha256, args.source_commit, args.run_id, args.run_attempt,
                             args.destination, args.retain_archive)
        else:
            result = runtime_lock_candidates(args.root, args.source_commit, args.run_id, args.run_attempt)
        print(json.dumps(result, sort_keys=True))
    except (OSError, ValueError, KeyError, TypeError, zipfile.BadZipFile) as error:
        print(json.dumps({"status": "Failed", "code": "c1_package_refused", "reason": str(error)}), file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
