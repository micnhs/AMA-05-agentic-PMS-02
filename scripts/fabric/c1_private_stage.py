"""Verify-only C1 artifact reception on the isolated Linux VM; no payload execution."""

import argparse
import base64
from contextlib import contextmanager
from datetime import datetime, timezone
from email.utils import format_datetime
import hashlib
import http.client
import ipaddress
import json
import os
from pathlib import Path
import re
import signal
import socket
import ssl
import stat
import sys
import tempfile
import threading
import time
import uuid
import zipfile

import c1_package as package


CONTRACT = "cohors-fabric-c1-private-stage-1"
ALLOWANCE_CONTRACT = "cohors-fabric-c1-stage-allowance-1"
ALLOWANCE_FIELDS = {
    "contract", "deploymentBindingHash", "invocationId", "profileSha256", "startedAtUtc",
    "expiresAtUtc", "requests", "budgetReviewSha256", "journalReceiptSha256",
}
STORAGE_AUDIENCE = "https://storage.azure.com/"
IMDS_HOST = "169.254.169.254"
IMDS_PATH = "/metadata/identity/oauth2/token?api-version=2018-02-01&resource=https%3A%2F%2Fstorage.azure.com%2F"
FIELDS = {
    "contract", "deploymentBindingHash", "vmResourceId", "storageAccountResourceId",
    "tenantId", "managedIdentityClientId", "managedIdentityObjectId", "expectedIssuer",
    "privateAddresses", "privatePathReviewSha256", "sourceCommit", "runId", "runAttempt",
    "outerArchiveSha256", "outerArchiveLength", "receiverSha256", "verifierSha256",
    "expiresAtUtc", "maxDurationSeconds",
}
PRIVATE_NETWORKS = tuple(ipaddress.ip_network(value) for value in
                         ("10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "fc00::/7"))


class StageError(Exception):
    def __init__(self, code):
        super().__init__(code)
        self.code = code


def require(condition, code):
    if not condition:
        raise StageError(code)


def hex_value(value, length):
    return isinstance(value, str) and re.fullmatch("[0-9a-f]{" + str(length) + "}", value) is not None


def guid(value):
    require(isinstance(value, str) and re.fullmatch(r"[0-9a-fA-F-]{36}", value) is not None,
            "c1_stage_identity_binding")
    parsed = uuid.UUID(value)
    require(parsed.int != 0, "c1_stage_identity_binding")
    return str(parsed)


def utc(value):
    require(isinstance(value, str) and
            re.fullmatch(r"\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d{1,7})?Z", value) is not None,
            "c1_stage_expiry")
    return datetime.fromisoformat(value.replace("Z", "+00:00"))


def private_address(value):
    address = ipaddress.ip_address(value)
    require(not isinstance(address, ipaddress.IPv6Address) or address.scope_id is None,
            "c1_stage_private_dns")
    require(any(address in network for network in PRIVATE_NETWORKS), "c1_stage_private_dns")
    return str(address)


def validate_profile(profile, now):
    require(isinstance(profile, dict) and set(profile) == FIELDS and profile["contract"] == CONTRACT,
            "c1_stage_profile")
    for field in ("deploymentBindingHash", "privatePathReviewSha256", "outerArchiveSha256",
                  "receiverSha256", "verifierSha256"):
        require(hex_value(profile[field], 64), "c1_stage_profile")
    require(hex_value(profile["sourceCommit"], 40) and
            all(isinstance(profile[field], str) and re.fullmatch(r"[1-9][0-9]*", profile[field])
                for field in ("runId", "runAttempt")), "c1_stage_source_binding")
    tenant = guid(profile["tenantId"])
    guid(profile["managedIdentityClientId"])
    guid(profile["managedIdentityObjectId"])
    require(profile["expectedIssuer"] in (f"https://sts.windows.net/{tenant}/",
                                          f"https://login.microsoftonline.com/{tenant}/v2.0"),
            "c1_stage_identity_binding")
    prefix = r"/subscriptions/([0-9a-fA-F-]{36})/resourceGroups/([A-Za-z0-9_-]{1,90})/providers/"
    vm = re.fullmatch(prefix + r"Microsoft\.Compute/virtualMachines/([A-Za-z0-9_-]{1,64})",
                      profile["vmResourceId"], re.IGNORECASE)
    storage = re.fullmatch(prefix + r"Microsoft\.Storage/storageAccounts/([a-z0-9]{3,24})",
                           profile["storageAccountResourceId"], re.IGNORECASE)
    require(vm is not None and storage is not None and guid(vm[1]) == guid(storage[1]) and
            vm[2].casefold() == storage[2].casefold(), "c1_stage_resource_binding")
    require(type(profile["outerArchiveLength"]) is int and
            0 < profile["outerArchiveLength"] <= package.MAX_ARCHIVE and
            type(profile["maxDurationSeconds"]) is int and 0 < profile["maxDurationSeconds"] <= 300,
            "c1_stage_limits")
    require(utc(profile["expiresAtUtc"]) > now, "c1_stage_expiry")
    addresses = profile["privateAddresses"]
    require(isinstance(addresses, list) and 0 < len(addresses) <= 16 and
            all(isinstance(value, str) for value in addresses), "c1_stage_private_dns")
    normalized = [private_address(value) for value in addresses]
    require(len(set(normalized)) == len(addresses), "c1_stage_private_dns")
    return storage[3].lower() + ".blob.core.windows.net"


def protected_directory(path):
    require(Path(path).is_absolute(), "c1_stage_private_path")
    directory = package.no_links(path)
    attributes = directory.stat()
    require(stat.S_ISDIR(attributes.st_mode) and attributes.st_uid == os.geteuid() and
            attributes.st_mode & 0o077 == 0, "c1_stage_private_path")
    return directory


def load_profile(path):
    require(Path(path).is_absolute(), "c1_stage_private_path")
    path = package.no_links(path)
    protected_directory(path.parent)
    attributes = path.stat()
    require(stat.S_ISREG(attributes.st_mode) and attributes.st_nlink == 1 and
            attributes.st_uid == os.geteuid() and attributes.st_mode & 0o077 == 0 and
            0 < attributes.st_size <= 16384, "c1_stage_private_path")
    with path.open("rb") as file:
        data = file.read(16385)
    require(len(data) <= 16384, "c1_stage_profile")
    return package.object_json(data)


class Budget:
    def __init__(self, profile, clock=None, monotonic=None):
        self.clock = clock or (lambda: datetime.now(timezone.utc))
        self.monotonic = monotonic or time.monotonic
        self.expiry = utc(profile["expiresAtUtc"])
        self.end = self.monotonic() + min(profile["maxDurationSeconds"],
                                          (self.expiry - self.clock()).total_seconds())

    def total_remaining(self):
        remaining = min(self.end - self.monotonic(), (self.expiry - self.clock()).total_seconds())
        require(remaining > 0, "c1_stage_deadline")
        return remaining

    def remaining(self):
        return min(self.total_remaining(), 30)


@contextmanager
def deadline(budget):
    require(sys.platform == "linux" and threading.current_thread() is threading.main_thread() and
            signal.getitimer(signal.ITIMER_REAL) == (0.0, 0.0), "c1_stage_deadline_handler")
    previous = signal.getsignal(signal.SIGALRM)

    def expired(*_):
        raise StageError("c1_stage_deadline")

    signal.signal(signal.SIGALRM, expired)
    try:
        signal.setitimer(signal.ITIMER_REAL, budget.total_remaining())
        yield
    finally:
        signal.setitimer(signal.ITIMER_REAL, 0)
        signal.signal(signal.SIGALRM, previous)


def resolve_private(host, allowed, budget):
    answer, failure = [], []
    completed = threading.Event()

    def resolve():
        try:
            answer.extend(socket.getaddrinfo(host, 443, type=socket.SOCK_STREAM))
        except OSError:
            failure.append(True)
        finally:
            completed.set()

    # A stalled OS resolver must not keep this bounded bootstrap process alive.
    threading.Thread(target=resolve, daemon=True).start()
    require(completed.wait(min(10, budget.remaining())) and not failure, "c1_stage_private_dns")
    addresses = sorted({private_address(item[4][0]) for item in answer})
    reviewed = {private_address(value) for value in allowed}
    require(0 < len(addresses) <= 16 and set(addresses).issubset(reviewed), "c1_stage_private_dns")
    budget.remaining()
    return addresses


class PrivateBlobConnection(http.client.HTTPSConnection):
    def __init__(self, host, address, budget):
        require(not os.environ.get("SSL_CERT_FILE") and not os.environ.get("SSL_CERT_DIR"),
                "c1_stage_tls_configuration")
        context = ssl.create_default_context()
        context.minimum_version = ssl.TLSVersion.TLSv1_2
        super().__init__(host, 443, timeout=budget.remaining(), context=context)
        self.address, self.budget = private_address(address), budget

    def connect(self):
        family = socket.AF_INET6 if ipaddress.ip_address(self.address).version == 6 else socket.AF_INET
        raw = socket.socket(family, socket.SOCK_STREAM)
        try:
            raw.settimeout(self.budget.remaining())
            raw.connect((self.address, 443))
            require(private_address(raw.getpeername()[0]) == self.address, "c1_stage_private_peer")
            raw.settimeout(self.budget.remaining())
            self.sock = self._context.wrap_socket(raw, server_hostname=self.host)
        except BaseException:
            raw.close()
            raise


def token_metadata(response, profile, now):
    require(response.get("resource") == STORAGE_AUDIENCE and response.get("token_type") == "Bearer",
            "c1_stage_token_binding")
    token = response.get("access_token")
    require(isinstance(token, str) and 0 < len(token) <= 32768 and
            re.fullmatch(r"[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+", token) is not None,
            "c1_stage_token_binding")
    header, payload, _ = token.split(".")
    header = package.object_json(base64.urlsafe_b64decode(header + "=" * (-len(header) % 4)))
    claims = package.object_json(base64.urlsafe_b64decode(payload + "=" * (-len(payload) % 4)))
    require(header.get("alg") == "RS256" and "scp" not in claims and claims.get("idtyp", "app") == "app" and
            claims.get("aud") == STORAGE_AUDIENCE and claims.get("iss") == profile["expectedIssuer"] and
            guid(claims.get("tid")) == guid(profile["tenantId"]) and
            guid(claims.get("oid")) == guid(profile["managedIdentityObjectId"]),
            "c1_stage_token_binding")
    clients = [claims[name] for name in ("appid", "azp") if name in claims]
    require(clients and all(guid(value) == guid(profile["managedIdentityClientId"]) for value in clients) and
            isinstance(claims.get("xms_mirid"), str) and
            claims["xms_mirid"].casefold() == profile["vmResourceId"].casefold(),
            "c1_stage_token_binding")
    require(type(claims.get("exp")) is int and type(claims.get("nbf")) is int and
            claims["nbf"] <= now.timestamp() < claims["exp"] and
            str(claims["exp"]) == response.get("expires_on"), "c1_stage_token_expiry")
    # IMDS is the sole token source. These are binding checks, not a local
    # signature/RBAC verdict; the private Storage endpoint must authorize it.
    return token


def response_length(response, maximum, code):
    lengths = response.headers.get_all("Content-Length", [])
    require(response.headers.get("Transfer-Encoding") is None and
            response.headers.get("Content-Encoding") in (None, "identity") and
            len(lengths) == 1 and re.fullmatch(r"[0-9]+", lengths[0]) is not None,
            code)
    length = int(lengths[0])
    require(0 < length <= maximum, code)
    return length


class AzureReceiver:
    def __init__(self, profile, host, budget):
        self.profile, self.host, self.budget = profile, host, budget
        self.connection = None

    def __enter__(self):
        self.addresses = resolve_private(self.host, self.profile["privateAddresses"], self.budget)
        self.connection = PrivateBlobConnection(self.host, self.addresses[0], self.budget)
        try:
            self.connection.connect()
        except BaseException:
            self.connection.close()
            raise
        return self

    def __exit__(self, *_):
        if self.connection is not None:
            self.connection.close()

    def token(self):
        connection = http.client.HTTPConnection(IMDS_HOST, timeout=self.budget.remaining())
        try:
            connection.request("GET", IMDS_PATH, headers={"Metadata": "true", "Accept": "application/json"})
            stream_socket = connection.sock
            with connection.getresponse() as response:
                require(response.status == 200, "c1_stage_imds_http_" + str(response.status))
                require(response.headers.get_content_type() == "application/json", "c1_stage_token_response")
                length = response_length(response, 65536, "c1_stage_token_response")
                data = bytearray()
                while len(data) < length:
                    stream_socket.settimeout(self.budget.remaining())
                    chunk = response.read1(min(65536, length - len(data)))
                    require(chunk, "c1_stage_token_response")
                    data.extend(chunk)
            return token_metadata(package.object_json(data), self.profile, self.budget.clock())
        finally:
            connection.close()

    def download(self, token, destination):
        profile = self.profile
        path = ("/c1-artifacts/" + profile["deploymentBindingHash"] + "/" + profile["sourceCommit"] +
                "/" + profile["outerArchiveSha256"] + ".zip")
        self.connection.sock.settimeout(self.budget.remaining())
        self.connection.request("GET", path, headers={
            "Authorization": "Bearer " + token, "Accept": "application/octet-stream",
            "x-ms-version": "2023-11-03", "x-ms-date": format_datetime(self.budget.clock(), usegmt=True),
        })
        stream_socket = self.connection.sock
        with self.connection.getresponse() as response:
            require(response.status == 200, "c1_stage_blob_http_" + str(response.status))
            length = response_length(response, package.MAX_ARCHIVE, "c1_stage_archive_length")
            require(length == profile["outerArchiveLength"], "c1_stage_archive_length")
            checksum, received = hashlib.sha256(), 0
            while received < length:
                stream_socket.settimeout(self.budget.remaining())
                chunk = response.read1(min(65536, length - received))
                require(chunk, "c1_stage_archive_length")
                destination.write(chunk)
                checksum.update(chunk)
                received += len(chunk)
            require(checksum.hexdigest() == profile["outerArchiveSha256"], "c1_stage_archive_digest")
        return {"resolvedAddresses": self.addresses, "connectedAddress": self.addresses[0]}


def stage(profile, destination, receiver_factory=AzureReceiver, clock=None, monotonic=None):
    clock = clock or (lambda: datetime.now(timezone.utc))
    validate_profile(profile, clock())
    budget = Budget(profile, clock, monotonic)
    with deadline(budget):
        return stage_payload(profile, destination, receiver_factory, budget)


def stage_payload(profile, destination, receiver_factory, budget):
    host = validate_profile(profile, budget.clock())
    for file, field in ((Path(__file__), "receiverSha256"), (Path(package.__file__), "verifierSha256")):
        require(package.digest(package.no_links(file).read_bytes()) == profile[field], "c1_stage_tool_binding")
    require(Path(destination).is_absolute(), "c1_stage_private_path")
    target = package.no_links(destination)
    parent = protected_directory(target.parent)
    require(not target.exists(), "c1_stage_new_destination")
    archive_path = None
    try:
        with tempfile.NamedTemporaryFile(prefix=".c1-stage-", suffix=".zip", dir=parent, delete=False) as archive:
            archive_path = Path(archive.name)
            with receiver_factory(profile, host, budget) as receiver:
                token = receiver.token()
                observation = receiver.download(token, archive)
                token = None
            archive.flush()
            os.fsync(archive.fileno())
        budget.remaining()
        require(archive_path.stat().st_size == profile["outerArchiveLength"], "c1_stage_archive_length")
        manifest, payload, receipt, data = package.verify_download(
            archive_path, profile["outerArchiveSha256"], profile["sourceCommit"], profile["runId"], profile["runAttempt"])
        del data
        require(manifest["contract"] == package.CONTRACT and
                package.digest(payload["assets/scripts/fabric/c1_private_stage.py"]) == profile["receiverSha256"] and
                package.digest(payload["assets/scripts/fabric/c1_package.py"]) == profile["verifierSha256"],
                "c1_stage_tool_binding")
        budget.remaining()
        package.extract(manifest, payload, receipt, target)
        budget.remaining()
        return {"status": "VerifiedNotExecuted", "contract": CONTRACT, "sourceCommit": profile["sourceCommit"],
                "runId": profile["runId"], "runAttempt": profile["runAttempt"], "files": len(payload),
                "profileSha256": package.digest(package.encoded(profile)), "archiveSha256": receipt["archiveSha256"],
                "outerArchiveSha256": profile["outerArchiveSha256"], "httpRequests": 2, "retries": 0,
                "privateTransport": observation, "payloadExecuted": False}
    finally:
        if archive_path is not None:
            archive_path.unlink()


def exclusive_document(path, data):
    try:
        descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
    except FileExistsError as error:
        raise StageError("c1_stage_claim_used") from error
    with os.fdopen(descriptor, "wb") as file:
        file.write(package.encoded(data))
        file.flush()
        os.fsync(file.fileno())
    directory = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
    try:
        os.fsync(directory)
    finally:
        os.close(directory)


def validate_allowance(profile, allowance, now):
    validate_profile(profile, now)
    require(isinstance(allowance, dict) and set(allowance) == ALLOWANCE_FIELDS and
            allowance["contract"] == ALLOWANCE_CONTRACT and
            allowance["deploymentBindingHash"] == profile["deploymentBindingHash"] and
            allowance["profileSha256"] == package.digest(package.encoded(profile)) and
            allowance["expiresAtUtc"] == profile["expiresAtUtc"] and
            type(allowance["requests"]) is int and allowance["requests"] == 2 and
            all(hex_value(allowance[field], 64) for field in ("budgetReviewSha256", "journalReceiptSha256")),
            "c1_stage_allowance")
    require(guid(allowance["invocationId"]) == allowance["invocationId"] and
            utc(allowance["startedAtUtc"]) <= now < utc(allowance["expiresAtUtc"]) and
            0 < (utc(allowance["expiresAtUtc"]) - utc(allowance["startedAtUtc"])).total_seconds()
            <= profile["maxDurationSeconds"], "c1_stage_allowance")


def stage_directory(profile):
    return Path("/var/lib/cohors-c1") / profile["deploymentBindingHash"] / "artifact-stage"


def stage_once(profile, allowance, destination, claim_directory, receiver_factory=AzureReceiver, clock=None, monotonic=None):
    clock = clock or (lambda: datetime.now(timezone.utc))
    validate_allowance(profile, allowance, clock())
    budget = Budget(profile, clock, monotonic)
    with deadline(budget):
        require(Path(claim_directory) == stage_directory(profile), "c1_stage_claim_directory")
        root = protected_directory(claim_directory)
        require(Path(destination).is_absolute() and package.no_links(destination).parent == root,
                "c1_stage_private_path")
        name = "stage-" + allowance["profileSha256"]
        # The protected control allowance is debited upstream. Persist its one-use
        # claim before DNS/token/Blob I/O; any unknown outcome remains spent.
        exclusive_document(root / (name + ".claim.json"), {
            "status": "Claimed", "allowance": allowance, "destination": str(destination),
        })
        budget.remaining()
        result = stage_payload(profile, destination, receiver_factory, budget)
        result.update(invocationId=allowance["invocationId"],
                      journalReceiptSha256=allowance["journalReceiptSha256"],
                      allowanceSha256=package.digest(package.encoded(allowance)))
        exclusive_document(root / (name + ".receipt.json"), result)
        budget.remaining()
        return result


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--profile", required=True)
    parser.add_argument("--destination", required=True)
    parser.add_argument("--allowance")
    args = parser.parse_args(argv)
    try:
        require(os.environ.get("GITHUB_ACTIONS") != "true", "c1_stage_ci_forbidden")
        require(sys.platform == "linux", "c1_stage_linux_required")
        require(args.allowance is not None, "c1_stage_allowance_required")
        require(Path(args.profile).parent == Path(args.allowance).parent, "c1_stage_claim_directory")
        result = stage_once(load_profile(args.profile), load_profile(args.allowance), args.destination,
                            Path(args.allowance).parent)
        print(json.dumps(result, sort_keys=True))
    except StageError as error:
        print(json.dumps({"status": "Blocked", "code": error.code, "payloadExecuted": False}), file=sys.stderr)
        return 2
    except KeyboardInterrupt:
        print(json.dumps({"status": "Interrupted", "code": "c1_stage_interrupted", "payloadExecuted": False}), file=sys.stderr)
        return 130
    except (OSError, ValueError, KeyError, TypeError, AttributeError, http.client.HTTPException, zipfile.BadZipFile):
        print(json.dumps({"status": "Blocked", "code": "c1_stage_incomplete", "payloadExecuted": False}), file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
