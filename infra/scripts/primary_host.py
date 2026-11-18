"""Private Linux execution worker. Uses only newly created, explicitly selected MIs."""

import argparse
import base64
import datetime as dt
import hashlib
import ipaddress
import json
import os
from pathlib import Path
import re
import shutil
import socket
import subprocess
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

from primary_control import (ARM, GateError, NoRedirect, canonical, digest, file_hash, jwt_metadata,
                             parse_time, read_json, timestamp, write_json)
from primary_package import verify_bundle


API_GRANTS = {
    "Aggregates": ("SELECT", "INSERT", "UPDATE"), "AggregateVersions": ("SELECT", "INSERT"),
    "Commands": ("SELECT", "INSERT"), "AuditEvents": ("SELECT", "INSERT"),
    "Outbox": ("SELECT", "INSERT", "UPDATE"), "Inbox": ("SELECT", "INSERT"),
    "Deadlines": ("SELECT", "INSERT", "UPDATE"), "SearchProjection": ("SELECT", "INSERT", "UPDATE"),
    "SchemaVersions": ("SELECT",),
}


class TransportError(GateError):
    """Transport did not yield a response; never substitute a successful phase result."""


def execute(arguments, *, env=None, input_bytes=None, timeout=600, pass_fds=()):
    try:
        result = subprocess.run(arguments, env=env, input=input_bytes, capture_output=True,
                                timeout=timeout, check=False, pass_fds=pass_fds)
    except (OSError, subprocess.TimeoutExpired) as error:
        raise GateError(f"Private tool {Path(arguments[0]).name} failed/timed out; effect may require reconciliation.") from error
    if result.returncode:
        raise GateError(f"Private tool {Path(arguments[0]).name} returned {result.returncode}; payload output withheld.")
    return result.stdout


def http(method, url, *, headers=None, data=None, allowed=(200,)):
    opener = urllib.request.build_opener(NoRedirect(), urllib.request.ProxyHandler({}))
    try:
        request = urllib.request.Request(url, method=method, headers=headers or {}, data=data)
        with opener.open(request, timeout=90) as response:
            status, response_headers, raw = response.status, dict(response.headers), response.read()
    except urllib.error.HTTPError as error:
        status, response_headers, raw = error.code, dict(error.headers), error.read()
    except (OSError, TimeoutError) as error:
        raise TransportError("Private HTTP result is uncertain; no automatic retry.") from error
    if status not in allowed:
        raise GateError(f"Private HTTP returned {status}; response body withheld.")
    return raw, response_headers, status


class ManagedIdentity:
    def __init__(self, identity, tenant):
        self.identity, self.tenant = identity, tenant

    def token(self, resource):
        if resource not in (ARM + "/", "https://monitor.azure.com/", "https://api.loganalytics.io/"):
            raise GateError("Unexpected private-worker token resource.")
        query = urllib.parse.urlencode({"api-version": "2018-02-01", "resource": resource,
                                        "client_id": self.identity["clientId"]})
        raw, _, _ = http("GET", "http://169.254.169.254/metadata/identity/oauth2/token?" + query,
                         headers={"Metadata": "true"})
        token = json.loads(raw)["access_token"]
        claims = jwt_metadata(token)
        if (claims["tid"] != self.tenant or claims["oid"] != self.identity["objectId"]
                or (claims["azp"] or claims["appid"]) != self.identity["clientId"]
                or claims["scp"] or claims["aud"] != resource or claims["exp"] <= time.time() + 120):
            raise GateError("Private-worker token is not the bound app-only managed identity.")
        return token


def require_private(host, network):
    addresses = {row[4][0] for row in socket.getaddrinfo(host, 443, family=socket.AF_INET,
                                                       type=socket.SOCK_STREAM)}
    if not addresses or any(ipaddress.ip_address(value) not in ipaddress.ip_network(network) for value in addresses):
        raise GateError("A private service did not resolve exclusively into this deployment's own VNet.")
    return sorted(addresses)


def sql_grants(role, client_id):
    if role != "api":
        raise GateError("Generic Operations JSON grants are prohibited. Use the primary owner's built restricted SQL contract.")
    sid = uuid.UUID(client_id).bytes_le.hex()
    name = "cohors_" + role
    grants = API_GRANTS
    lines = ["SET XACT_ABORT ON; BEGIN TRANSACTION;",
             f"IF EXISTS(SELECT 1 FROM sys.database_principals WHERE name=N'{name}' AND (sid<>0x{sid} OR type<>'E')) THROW 51000,'Runtime identity mismatch',1;",
             f"IF NOT EXISTS(SELECT 1 FROM sys.database_principals WHERE name=N'{name}') CREATE USER [{name}] WITH SID=0x{sid}, TYPE=E;",
             f"GRANT CONNECT TO [{name}];"]
    lines += [f"GRANT {','.join(permissions)} ON OBJECT::dbo.[{table}] TO [{name}];"
              for table, permissions in grants.items()]
    lines += [f"DENY DELETE ON SCHEMA::dbo TO [{name}];", f"DENY ALTER ON SCHEMA::dbo TO [{name}];",
              f"DENY INSERT,UPDATE,DELETE ON OBJECT::dbo.SchemaVersions TO [{name}];", "COMMIT;"]
    return "\n".join(lines)


def sql_run(bundle, request, sql):
    platform = request["platform"]
    executable = bundle / "tools/sql/Cohors.InfraSql"
    executable.chmod(0o700)
    execute([str(executable), platform["endpoints"]["sqlServer"], platform["databaseName"],
             request["identities"]["bootstrap"]["clientId"]],
            env=runtime_environment(request, request["identities"]["bootstrap"], bundle), input_bytes=sql.encode(), timeout=300)


def dotnet_environment(bundle):
    root = bundle / "tools/dotnet"
    (root / "dotnet").chmod(0o700)
    return {"DOTNET_ROOT": str(root), "DOTNET_ROOT_X64": str(root), "DOTNET_ROLL_FORWARD": "LatestPatch",
            "DOTNET_MULTILEVEL_LOOKUP": "0", "DOTNET_NOLOGO": "1", "DOTNET_CLI_TELEMETRY_OPTOUT": "1"}


def runtime_environment(request, identity, bundle):
    endpoints, directory = request["platform"]["endpoints"], request["directory"]
    environment = {**dotnet_environment(bundle), "PATH": os.environ["PATH"], "HOME": "/var/lib/cohors-primary/empty-home",
                   "ASPNETCORE_ENVIRONMENT": "Production", "DOTNET_ENVIRONMENT": "Production",
                   "AZURE_CLIENT_ID": identity["clientId"], "AZURE_TOKEN_CREDENTIALS": "ManagedIdentityCredential",
                   "Cohors__SqlAuthentication": "ManagedIdentity",
                   "ConnectionStrings__Cohors": f"Server=tcp:{endpoints['sqlServer']},1433;Database={request['platform']['databaseName']};Encrypt=true;TrustServerCertificate=false;",
                   "Search__Endpoint": endpoints["search"], "Search__Index": "cohors-evidence",
                   "Cohors__ObjectStore": "Azure", "Blob__ContainerUri": endpoints["evidenceContainer"],
                   "Cohors__Search": "Azure", "Cohors__AutoMigrate": "false", "Cohors__Seed": "false",
                   "Cohors__LocalOperations": "false", "Cohors__PublicSources": "false",
                   "Authentication__Mode": "Entra", "Entra__TenantId": request["tenantId"],
                   "Entra__Audience": directory["api"]["clientId"], "Entra__SpaClientId": directory["spa"]["clientId"],
                   "Entra__Scope": "access_as_user", "Cohors__AgentTransport": "Remote",
                   "Agents__Scope": "api://" + directory["agents"]["clientId"] + "/.default",
                   "Logging__LogLevel__Default": "None", "APPLICATIONINSIGHTS_CONFIGURATION_CONTENT": ""}
    Path(environment["HOME"]).mkdir(parents=True, exist_ok=True)
    return environment


def initialize_schema(bundle, request):
    env = runtime_environment(request, request["identities"]["bootstrap"], bundle)
    executable = bundle / "bootstrap/Cohors.Bootstrap"
    executable.chmod(0o700)
    endpoints = request["platform"]["endpoints"]
    execute([str(executable), "sql", "--expect-server", endpoints["sqlServer"], "--expect-database",
             request["platform"]["databaseName"], "--apply"], env=env)
    execute([str(executable), "search", "--expect-endpoint", endpoints["search"],
             "--expect-index", "cohors-evidence", "--apply"], env=env)
    # These are identity/object permissions only. Application migrations above remain authoritative.
    sql_run(bundle, request, sql_grants("api", request["runtimeIdentities"]["api"]["clientId"]))
    return {"apiPermissionsApplied": True, "operationsPermissionsApplied": False,
            "operationsRequiresBuiltRestrictedContract": True}


def seed(bundle, request):
    executable = bundle / "api/Cohors.Api"
    executable.chmod(0o700)
    env = runtime_environment(request, request["identities"]["bootstrap"], bundle)
    env["Agents__Endpoint"] = request["platform"]["runtimeUrls"]["agents"]
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as reservation:
        reservation.bind(("127.0.0.1", 0))
        port = reservation.getsockname()[1]
    env.update(COHORS_HOME=str(bundle / "api"), ASPNETCORE_URLS=f"http://127.0.0.1:{port}", Cohors__Seed="true")
    if not (bundle / "api/demo").is_dir():
        raise GateError("The bound synthetic demo input is missing from the API package.")
    process = subprocess.Popen([str(executable)], env=env, stdin=subprocess.DEVNULL,
                               stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    try:
        deadline = time.monotonic() + 120
        while time.monotonic() < deadline:
            if process.poll() is not None:
                raise GateError("The isolated synthetic seed process exited before startup completed.")
            try:
                raw, _, status = http("GET", f"http://127.0.0.1:{port}/health/live")
            except TransportError:
                time.sleep(2)
                continue
            if status == 200:
                sockets = {os.readlink(item) for item in (Path("/proc") / str(process.pid) / "fd").iterdir()
                           if item.is_symlink()}
                rows = (Path("/proc") / str(process.pid) / "net/tcp").read_text().splitlines()[1:]
                if process.poll() is not None or not any(
                        parts[1] == f"0100007F:{port:04X}" and parts[3] == "0A"
                        and "socket:[" + parts[9] + "]" in sockets for row in rows if (parts := row.split())):
                    raise GateError("The readiness listener is not owned by the isolated bound seed process.")
                return {"entryPoint": "Cohors.Api startup Cohors__Seed=true", "startupCompleted": True,
                        "runtimeSeedRemainsDisabled": True}
        raise GateError("Synthetic baseline startup did not complete before its bound timeout.")
    finally:
        process.terminate()
        try:
            process.wait(timeout=15)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=10)


def push_images(bundle, request, release):
    registry = request["platform"]["endpoints"]["registry"]
    require_private(registry, request["sourceNetwork"])
    identity = ManagedIdentity(request["identities"]["publisher"], request["tenantId"])
    body = urllib.parse.urlencode({"grant_type": "access_token", "service": registry,
                                   "tenant": request["tenantId"], "access_token": identity.token(ARM + "/")}).encode()
    raw, _, _ = http("POST", "https://" + registry + "/oauth2/exchange",
                     headers={"Content-Type": "application/x-www-form-urlencoded"}, data=body)
    refresh = json.loads(raw)["refresh_token"]
    authentication = base64.b64encode(("00000000-0000-0000-0000-000000000000:" + refresh).encode()).decode()
    fd = os.memfd_create("cohors-acr-auth", flags=0)
    try:
        os.write(fd, canonical({"auths": {registry: {"auth": authentication}}}))
        result = {}
        for host in ("api", "agents"):
            target = f"docker://{registry}/cohors-{host}:{release['applicationCommit']}"
            args = ["skopeo", "copy", "--all", "--preserve-digests", "--authfile", f"/proc/self/fd/{fd}",
                    f"oci-archive:{bundle / ('images/' + host + '.oci.tar')}", target]
            execute(args, pass_fds=(fd,), timeout=900)
            raw_manifest = execute(["skopeo", "inspect", "--raw", "--authfile", f"/proc/self/fd/{fd}",
                                    target], pass_fds=(fd,))
            actual = "sha256:" + hashlib.sha256(raw_manifest).hexdigest()
            if actual != release["images"][host]:
                raise GateError("ACR manifest digest differs from the built OCI artifact.")
            result[host + "ImageDigest"] = actual.removeprefix("sha256:")
        return result
    finally:
        os.close(fd)


def boundary_hash(bundle, request):
    review = request["boundaryReview"]
    env = {**dotnet_environment(bundle), "PATH": os.environ["PATH"], "DOTNET_ENVIRONMENT": "Production",
           "Foundry__Endpoint": request["platform"]["endpoints"]["foundryOpenAi"],
           "Foundry__Deployment": request["model"]["deploymentName"],
           "Foundry__ModelVersion": request["model"]["version"], "Foundry__ProcessingBoundary": "EU-Member-States"}
    for key in ("Reference", "DeploymentSku", "DeploymentRegion", "DeploymentGeography"):
        env["Foundry__BoundaryReview__" + key] = review[key[0].lower() + key[1:]]
    for index, country in enumerate(review["processingCountries"]):
        env[f"Foundry__BoundaryReview__ProcessingCountries__{index}"] = country
    env["Foundry__BoundaryReview__ConfigurationHash"] = "pending"
    executable = bundle / "agents/Cohors.AgentHost"
    executable.chmod(0o700)
    result = execute([str(executable), "--boundary-hash"], env=env).decode().strip()
    if not re.fullmatch(r"[0-9a-f]{64}", result):
        raise GateError("The real AgentHost did not emit one boundary hash.")
    return {**review, "configurationHash": result}


def publish_functions(bundle, request, release):
    result = {}
    identity = ManagedIdentity(request["identities"]["publisher"], request["tenantId"])
    for host in ("workflows", "operations"):
        site = urllib.parse.urlsplit(request["platform"]["runtimeUrls"][host]).hostname
        scm = site.removesuffix(".azurewebsites.net") + ".scm.azurewebsites.net"
        require_private(scm, request["sourceNetwork"])
        package = bundle / f"functions/cohors-{host}.zip"
        query = urllib.parse.urlencode({"type": "zip", "isAsync": "true"})
        raw, headers, _ = http("POST", f"https://{scm}/api/publish?{query}",
                               headers={"Authorization": "Bearer " + identity.token(ARM + "/"),
                                        "Content-Type": "application/zip"}, data=package.read_bytes(),
                               allowed=(200, 202))
        poll = headers.get("Location") or headers.get("location")
        if not poll:
            raise GateError("OneDeploy response has no deployment-specific polling URL; no inferred publish success.")
        poll = urllib.parse.urljoin("https://" + scm, poll)
        if urllib.parse.urlsplit(poll).hostname != scm or not poll.startswith("https://" + scm + "/api/"):
            raise GateError("OneDeploy continuation is outside the selected private SCM service.")
        until = time.monotonic() + 600
        while time.monotonic() < until:
            raw, _, _ = http("GET", poll, headers={"Authorization": "Bearer " + identity.token(ARM + "/")})
            state = json.loads(raw).get("status")
            if state == 4:
                result[host] = {"zipSha256": release["functions"][host]["sha256"], "oneDeploySucceeded": True}
                break
            if state == 3:
                raise GateError("OneDeploy failed; no artifact/blob-upload success substitution.")
            if state not in (0, 1, 2):
                raise GateError("Unknown OneDeploy status; explicit reconciliation required.")
            time.sleep(10)
        else:
            raise GateError("OneDeploy timeout; do not blindly publish again.")
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bundle", type=Path, required=True)
    parser.add_argument("--request", type=Path, required=True)
    args = parser.parse_args()
    request = read_json(args.request)
    release = verify_bundle(args.bundle, request["applicationCommit"], request["infrastructureCommit"])
    raw, _, _ = http("GET", "http://169.254.169.254/metadata/instance/compute?api-version=2021-02-01",
                     headers={"Metadata": "true"})
    instance = json.loads(raw)
    if instance.get("resourceId", "").lower() != request["vmId"].lower():
        raise GateError("Private execution is not on the journal-owned VM.")
    if dict((item["name"], item["value"]) for item in instance.get("tagsList", [])).get("cohorsBinding") != request["bindingHash"]:
        raise GateError("Private VM ownership marker changed.")
    phase = request["phase"]
    receipt = {"contract": "cohors-primary-private-receipt-1", "bindingHash": request["bindingHash"],
               "phase": phase, "requestHash": digest(request), "atUtc": timestamp(),
               "applicationCommit": release["applicationCommit"], "infrastructureCommit": release["infrastructureCommit"]}
    if phase == "filter":
        import sys
        sys.path.insert(0, str(args.bundle / "infra/bootstrap"))
        from install_nva import install
        receipt["result"] = install(args.bundle, request["nvaPolicy"])
    else:
        for key in ("search", "evidenceContainer", "foundryOpenAi"):
            require_private(urllib.parse.urlsplit(request["platform"]["endpoints"][key]).hostname,
                            request["sourceNetwork"])
        require_private(request["platform"]["endpoints"]["sqlServer"], request["sourceNetwork"])
        if phase == "initialize":
            if not shutil.which("skopeo"):
                execute(["apt-get", "update", "-qq"])
                execute(["apt-get", "install", "-y", "-qq", "skopeo"])
            images = push_images(args.bundle, request, release)
            permissions = initialize_schema(args.bundle, request)
            receipt["result"] = {"release": {**images, "boundaryReview": boundary_hash(args.bundle, request)},
                                 "sqlPermissions": permissions}
        elif phase == "seed":
            receipt["result"] = seed(args.bundle, request)
        elif phase == "publish-functions":
            receipt["result"] = publish_functions(args.bundle, request, release)
        else:
            raise GateError("Unsupported private execution phase.")
    print("COHORS_RECEIPT:" + base64.b64encode(canonical(receipt)).decode())


if __name__ == "__main__":
    try:
        main()
    except GateError as error:
        print("COHORS_GATE:" + str(error))
        raise SystemExit(2)
