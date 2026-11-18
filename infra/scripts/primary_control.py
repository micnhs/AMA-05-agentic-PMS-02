"""Bound Azure control-plane access and crash-safe, non-secret bootstrap state."""

from __future__ import annotations

import base64
import contextlib
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid


ARM = "https://management.azure.com"
GRAPH = "https://graph.microsoft.com"
UTC = dt.timezone.utc
SAFE_CODE = re.compile(r"[A-Za-z0-9_.-]{1,120}\Z")
GUID = re.compile(r"[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}\Z")


class GateError(Exception):
    """A bounded failure safe to display without a service payload."""


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=True).encode()


def digest(value):
    return hashlib.sha256(canonical(value)).hexdigest()


def file_hash(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def utcnow():
    return dt.datetime.now(UTC)


def timestamp(value=None):
    return (value or utcnow()).isoformat(timespec="seconds").replace("+00:00", "Z")


def parse_time(value):
    try:
        result = dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
    except (AttributeError, TypeError, ValueError) as error:
        raise GateError("Invalid UTC timestamp.") from error
    if result.utcoffset() != dt.timedelta(0):
        raise GateError("Timestamp must explicitly use UTC.")
    return result


def require_guid(value, label):
    if not isinstance(value, str) or not GUID.fullmatch(value):
        raise GateError(f"{label} must be a lowercase GUID.")
    return value


def read_json(path):
    path = Path(path)
    if path.is_symlink() or not path.is_file():
        raise GateError("Expected a regular, non-symlink JSON file.")
    try:
        return json.loads(path.read_text(encoding="utf-8-sig"))
    except (UnicodeError, json.JSONDecodeError) as error:
        raise GateError("Invalid UTF-8 JSON file.") from error


def write_json(path, value, *, replace=False):
    """Exclusive first write; atomic updates only for caller-owned files."""
    path = Path(path)
    if path.is_symlink() or (path.exists() and not replace):
        raise GateError("Refusing to overwrite an existing artifact.")
    path.parent.mkdir(parents=True, exist_ok=True)
    encoded = json.dumps(value, ensure_ascii=True, indent=2).encode() + b"\n"
    if not replace:
        fd = os.open(path, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
        with os.fdopen(fd, "wb") as stream:
            stream.write(encoded)
            stream.flush()
            os.fsync(stream.fileno())
        return
    fd, temporary = tempfile.mkstemp(prefix=f".{path.name}.", dir=path.parent)
    try:
        with os.fdopen(fd, "wb") as stream:
            stream.write(encoded)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def jwt_metadata(token):
    try:
        pieces = token.split(".")
        if len(pieces) != 3:
            raise ValueError()
        claims = json.loads(base64.urlsafe_b64decode(pieces[1] + "=" * (-len(pieces[1]) % 4)))
        return {key: claims.get(key) for key in ("tid", "oid", "appid", "azp", "aud", "iss", "scp", "exp", "amr", "acrs")}
    except (ValueError, TypeError, UnicodeError) as error:
        raise GateError("Could not inspect the acquired token's permitted binding claims.") from error


def control_claims(binding, observed, resource):
    if resource not in (ARM + "/", GRAPH + "/") or not isinstance(observed, dict):
        raise GateError("Explicit observed ARM/Graph audience and issuer bindings are required.")
    service = "arm" if resource == ARM + "/" else "graph"
    audience, issuer = observed.get(service + "Audience"), observed.get(service + "Issuer")
    allowed = ({ARM, ARM + "/", "https://management.core.windows.net/",
                "797f4846-ba00-4fd7-ba43-dac1f8f63013"} if service == "arm" else
               {GRAPH, GRAPH + "/", "00000003-0000-0000-c000-000000000000"})
    issuers = {f"https://sts.windows.net/{binding['tenantId']}/",
               f"https://login.microsoftonline.com/{binding['tenantId']}/v2.0"}
    if not isinstance(audience, str) or audience not in allowed or not isinstance(issuer, str) or issuer not in issuers:
        raise GateError("Observed control-token binding is not this tenant's selected ARM/Graph authority.")
    return audience, issuer


class BoundCli:
    """Uses the existing delegated CLI context; never logs in or selects a subscription."""

    def __init__(self, binding, control_token_bindings):
        self.binding = binding
        self.control_token_bindings = control_token_bindings
        for resource in (ARM + "/", GRAPH + "/"):
            control_claims(binding, control_token_bindings, resource)
        self.metadata = {}
        self._tokens = {}
        self._extensions = tempfile.TemporaryDirectory(prefix="cohors-extensions-")
        command = shutil.which("az")
        if not command:
            raise GateError("Azure CLI is required; no login or automatic installation was attempted.")
        backend = Path(command).resolve().parent.parent / "python.exe"
        self.command = ([str(backend), "-IBm", "azure.cli"] if os.name == "nt" and backend.is_file()
                        else [command])

    def close(self):
        self._tokens.clear()
        self._extensions.cleanup()

    def cli(self, arguments):
        env = os.environ.copy()
        env.update(AZURE_EXTENSION_DIR=self._extensions.name,
                   AZURE_EXTENSION_USE_DYNAMIC_INSTALL="no",
                   AZURE_CORE_COLLECT_TELEMETRY="no")
        try:
            result = subprocess.run(self.command + arguments + ["--only-show-errors", "-o", "json"],
                                    stdin=subprocess.DEVNULL, capture_output=True,
                                    timeout=90, env=env, check=False)
        except (OSError, subprocess.TimeoutExpired) as error:
            raise GateError("Existing-context CLI operation failed or timed out; no retry.") from error
        if result.returncode:
            # stderr may contain authentication URLs, claims challenges or personal data.
            raise GateError("Existing-context CLI operation failed; reauthentication/consent was not attempted.")
        try:
            return json.loads(result.stdout)
        except (ValueError, UnicodeError) as error:
            raise GateError("Azure CLI did not return valid JSON.") from error

    def check_context(self):
        account = self.cli(["account", "show"])
        if (account.get("id") != self.binding["subscriptionId"]
                or account.get("tenantId") != self.binding["tenantId"]
                or account.get("state") != "Enabled"
                or account.get("environmentName") != "AzureCloud"
                or account.get("user", {}).get("type") != "user"):
            raise GateError("Current Azure context differs from the selected delegated initial deployer; stopped.")
        return {key: account.get(key) for key in ("id", "tenantId", "state", "environmentName")}

    def token(self, resource):
        if resource not in (ARM + "/", GRAPH + "/"):
            raise GateError("The initial deployer can request only the bound ARM and Graph resources.")
        cached = self._tokens.get(resource)
        if cached and self.metadata[resource]["exp"] > time.time() + 180:
            return cached
        self.check_context()
        response = self.cli(["account", "get-access-token", "--resource", resource])
        token = response.get("accessToken", "")
        metadata = jwt_metadata(token)
        audience, issuer = control_claims(self.binding, self.control_token_bindings, resource)
        if (metadata.get("tid") != self.binding["tenantId"]
                or metadata.get("oid") != self.binding["principalObjectId"]
                or (metadata.get("azp") or metadata.get("appid")) != self.binding["oauthClientId"]
                or (metadata.get("azp") and metadata.get("appid") and metadata["azp"] != metadata["appid"])
                or metadata.get("aud") != audience
                or metadata.get("iss") != issuer
                or not metadata.get("scp")
                or not isinstance(metadata.get("exp"), int)
                or metadata["exp"] <= time.time() + 120):
            raise GateError("Acquired token does not match the selected delegated tenant/principal/client/audience.")
        self.metadata[resource] = metadata
        self._tokens[resource] = token
        return token


class Azure:
    def __init__(self, credential, *, allow_writes=False):
        self.credential = credential
        self.allow_writes = allow_writes
        self.observations = []
        self._opener = urllib.request.build_opener(NoRedirect())

    def request(self, method, url, body=None, *, allowed=(200,), headers=None):
        parsed = urllib.parse.urlsplit(url)
        if parsed.scheme != "https" or parsed.netloc not in ("management.azure.com", "graph.microsoft.com"):
            raise GateError("Control request target is outside the ARM/Graph allowlist.")
        if parsed.fragment or parsed.username or parsed.password:
            raise GateError("Invalid control request URL.")
        if method != "GET" and not self.allow_writes:
            raise GateError("Cloud mutation blocked: this client is read-only.")
        resource = f"https://{parsed.netloc}/"
        request_headers = {"Authorization": f"Bearer {self.credential.token(resource)}",
                           "Accept": "application/json", "Content-Type": "application/json"}
        request_headers.update(headers or {})
        if any(key.lower() == "authorization" for key in (headers or {})):
            raise GateError("Caller cannot replace the bound authentication header.")
        request = urllib.request.Request(url, data=canonical(body) if body is not None else None,
                                         headers=request_headers, method=method)
        try:
            with self._opener.open(request, timeout=90) as response:
                status, response_headers, raw = response.status, response.headers, response.read()
        except urllib.error.HTTPError as error:
            status, response_headers, raw = error.code, error.headers, error.read()
        except (OSError, TimeoutError) as error:
            raise GateError(f"{method} transport result is uncertain; no automatic retry.") from error
        try:
            content = json.loads(raw) if raw else {}
        except (UnicodeError, ValueError) as error:
            raise GateError(f"{method} returned HTTP {status} with a non-JSON body.") from error
        code = content.get("error", {}).get("code") if isinstance(content, dict) else None
        code = code if isinstance(code, str) and SAFE_CODE.fullmatch(code) else None
        observation = {"method": method, "url": url, "httpStatus": status,
                       "errorCode": code, "atUtc": timestamp(),
                       "claimsChallenge": bool(response_headers.get("WWW-Authenticate"))}
        self.observations.append(observation)
        if status not in allowed:
            raise GateError(f"{method} returned HTTP {status}/{code or 'NoSafeErrorCode'}; no retry or consent.")
        return content, dict(response_headers), status

    def get(self, url, *, allowed=(200,)):
        return self.request("GET", url, allowed=allowed)[0]

    def collection(self, url, *, max_pages=100):
        values = []
        first = urllib.parse.urlsplit(url)
        visited = set()
        for _ in range(max_pages):
            if url in visited:
                raise GateError("Pagination loop.")
            visited.add(url)
            result = self.get(url)
            if not isinstance(result.get("value"), list):
                raise GateError("Collection response has no value array.")
            values.extend(result["value"])
            next_url = result.get("@odata.nextLink") or result.get("nextLink")
            if not next_url:
                return values
            parsed = urllib.parse.urlsplit(next_url)
            if parsed.scheme != first.scheme or parsed.netloc != first.netloc or parsed.path != first.path:
                raise GateError("Collection continuation changed host or resource path.")
            url = next_url
        raise GateError("Collection page limit exceeded; result is incomplete.")

    def poll(self, url, *, timeout_seconds=1800):
        until = time.monotonic() + timeout_seconds
        while time.monotonic() < until:
            result = self.get(url)
            state = result.get("status") or result.get("properties", {}).get("provisioningState")
            if state == "Succeeded":
                return result
            if state in ("Failed", "Canceled", "Cancelled"):
                raise GateError(f"Azure asynchronous operation ended with {state}.")
            if state not in ("Accepted", "Running", "InProgress", "Creating", "Updating", "Deleting"):
                raise GateError("Unknown asynchronous operation state; reconciliation required.")
            time.sleep(10)
        raise GateError("Azure operation timeout; checkpoint retained for explicit resume.")


class Journal:
    """Intent is flushed before an effect; an ambiguous effect is never silently repeated."""

    def __init__(self, path, binding_hash):
        self.path = Path(path)
        self.binding_hash = binding_hash
        self.state = None
        self._lock = None

    def __enter__(self):
        self.path.parent.mkdir(parents=True, exist_ok=True)
        lock_path = self.path.with_name(self.path.name + ".lock")
        if self.path.is_symlink() or lock_path.is_symlink():
            raise GateError("Journal and lock must not be symbolic links.")
        self._lock = open(lock_path, "a+b")
        self._lock.seek(0)
        if self._lock.read(1) == b"":
            self._lock.write(b"\0")
            self._lock.flush()
        self._lock.seek(0)
        try:
            if os.name == "nt":
                import msvcrt
                msvcrt.locking(self._lock.fileno(), msvcrt.LK_NBLCK, 1)
            else:
                import fcntl
                fcntl.flock(self._lock.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        except OSError as error:
            self._lock.close()
            raise GateError("Another process owns this journal.") from error
        try:
            if self.path.exists():
                self.state = read_json(self.path)
                if (self.state.get("contract") != "cohors-primary-journal-1"
                        or self.state.get("bindingHash") != self.binding_hash):
                    raise GateError("Journal belongs to a different immutable deployment binding.")
                previous = "0" * 64
                for event in self.state["events"]:
                    unsigned = {key: value for key, value in event.items() if key != "sha256"}
                    if unsigned["previousSha256"] != previous or digest(unsigned) != event["sha256"]:
                        raise GateError("Journal hash chain is invalid.")
                    previous = event["sha256"]
                expected_outputs = {event["operation"]: event["details"]["output"]
                                    for event in self.state["events"] if event["kind"] == "completed"}
                if self.state["outputs"] != expected_outputs:
                    raise GateError("Journal outputs differ from their completed events.")
            else:
                self.state = {"contract": "cohors-primary-journal-1", "bindingHash": self.binding_hash,
                              "createdAtUtc": timestamp(), "events": [], "outputs": {}}
                write_json(self.path, self.state)
            return self
        except BaseException:
            self.__exit__(None, None, None)
            raise

    def __exit__(self, exc_type, exc_value, traceback):
        if self._lock:
            self._lock.close()
            self._lock = None

    def append(self, kind, operation, details):
        events = self.state["events"]
        event = {"sequence": len(events) + 1, "atUtc": timestamp(), "kind": kind,
                 "operation": operation, "details": details,
                 "previousSha256": events[-1]["sha256"] if events else "0" * 64}
        event["sha256"] = digest(event)
        events.append(event)
        write_json(self.path, self.state, replace=True)

    def last(self, operation):
        return next((event for event in reversed(self.state["events"])
                     if event["operation"] == operation), None)

    def begin(self, operation, method, target, request_body):
        request_hash = digest({"method": method, "target": target, "body": request_body})
        previous = self.last(operation)
        if previous:
            if previous["details"].get("requestHash") != request_hash:
                raise GateError("Operation payload changed since its checkpoint.")
            if previous["kind"] == "completed":
                return False
            raise GateError(f"Operation {operation} has an unresolved intent; inspect/reconcile, do not repeat.")
        self.append("intent", operation, {"method": method, "target": target, "requestHash": request_hash})
        return True

    def complete(self, operation, output):
        previous = self.last(operation)
        if not previous or previous["kind"] != "intent":
            raise GateError("No matching effect intent.")
        self.state["outputs"][operation] = output
        self.append("completed", operation, {"requestHash": previous["details"]["requestHash"], "output": output})


@contextlib.contextmanager
def bound_client(binding, control_token_bindings, *, allow_writes=False):
    credential = BoundCli(binding, control_token_bindings)
    try:
        credential.check_context()
        yield Azure(credential, allow_writes=allow_writes)
    finally:
        credential.close()
