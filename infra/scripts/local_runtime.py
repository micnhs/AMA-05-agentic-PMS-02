"""Local T12 host execution, not Actions, Azure, T20 recovery or a cloud SLA."""

import argparse
import base64
import hashlib
import json
import os
import re
import secrets
import shutil
import subprocess
import sys
import tarfile
import time
import uuid
from datetime import datetime, timezone
from pathlib import Path


SOURCE_SHA = "245b3b049228c3d4e01883bec56babca2012b91b"
SOURCE_PARENT = "a5c75454553e1325246546578a8ab3ef624ab119"
LABEL = "cohors.t12.run"
BASE_IMAGES = {
    "sql": "mcr.microsoft.com/mssql/server:2022-latest",
    "azurite": "mcr.microsoft.com/azure-storage/azurite:3.35.0",
    "driver": "node:22-bookworm-slim",
}
HOSTS = {"api": "Api", "agents": "AgentHost", "workflows": "Workflows", "operations": "Operations"}
ROOT = Path(__file__).resolve().parents[2]


class RuntimeFailure(RuntimeError):
    pass


def require(condition, message):
    if not condition:
        raise RuntimeFailure(message)


def now():
    return datetime.now(timezone.utc).isoformat()


def require_local_context(actions):
    require(actions != "true", "This is explicitly a local harness, not GitHub Actions.")


def memory_bytes(value):
    match = re.fullmatch(r"\s*(\d+(?:\.\d+)?)\s*(B|KiB|MiB|GiB|TiB)\s*", value)
    require(match is not None, "Unrecognized Docker memory measurement.")
    return float(match[1]) * 1024 ** ["B", "KiB", "MiB", "GiB", "TiB"].index(match[2])


def verify_business(snapshot):
    require(snapshot["receipts"] == 1 and snapshot["drafts"] == 1 and snapshot["starts"] == 1,
            "Exactly one receipt, assessment draft and agent-run claim are required.")
    require(snapshot["assessments"] == 1 and snapshot["attempts"] == 1 and snapshot["completed"],
            "The original outbox attempt must be committed exactly once.")
    require(len(snapshot["invocations"]) == 3 and all(
        item["status"] == "Completed" and item["mode"] == "Replay" and item["model"] == "replay-2026.1"
        for item in snapshot["invocations"]), "All three real remote replay invocations must be completed.")
    require({item["role"] for item in snapshot["invocations"]} == {"Ingestion", "CausalityRisk", "EvidenceAssurance"},
            "The three distinct assessment roles must actually execute.")


def verify_resume(before, after, transport_before, transport_after):
    verify_business(before)
    verify_business(after)
    require(before == after, "Workflow restart changed the committed business effect.")
    require(transport_before["held"] and transport_before["calls"] == 1, "The real response was not held before acknowledgement.")
    require(transport_after["calls"] >= 2 and transport_after["closed"], "No real activity redelivery after host termination.")
    require(transport_before["agentCalls"] == transport_after["agentCalls"] and len(transport_after["agentCalls"]) == 3,
            "Redelivery must reuse persisted results without a second model invocation.")
    require({(item["inputHash"], item["role"]) for item in before["invocations"]} ==
            {(item["inputHash"], item["role"]) for item in transport_after["agentCalls"]},
            "Observed AgentHost requests do not match the persisted SQL invocations.")


def filtered_json(value, prohibited):
    text = json.dumps(value, ensure_ascii=True, indent=2)
    require(not any(secret and secret in text for secret in prohibited), "Credential detected; evidence publication refused.")
    require(not re.search(r'(?i)"(?:password|workloadkey|storagekey|controlkey|cookie|authorization|csrfToken)"\s*:', text),
            "Authentication field detected; evidence publication refused.")
    return text + "\n"


class Harness:
    def __init__(self, workspace, artifacts):
        self.workspace = workspace.resolve()
        self.artifacts = artifacts.resolve()
        require_local_context(os.environ.get("GITHUB_ACTIONS"))
        require(not self.workspace.exists() and not self.artifacts.exists(), "Use new workspace and evidence directories.")
        require(self.workspace != self.artifacts and self.workspace not in self.artifacts.parents and
                self.artifacts not in self.workspace.parents, "Evidence must be separate from the disposable runtime workspace.")
        require(ROOT != self.workspace and ROOT not in self.workspace.parents, "Runtime state must stay outside the source worktree.")
        require(ROOT != self.artifacts and ROOT not in self.artifacts.parents, "Runtime evidence must stay outside the source worktree.")
        self.run = uuid.uuid4().hex
        self.prefix = "cohors-t12-" + self.run
        self.private = self.workspace / "private"
        self.source = self.workspace / "source"
        self.prohibited = []
        self.containers = {}
        self.volumes = []
        self.tags = []
        self.images = {}
        self.phase = "preflight"
        self.last_diagnostic = []
        self.evidence = {"schemaVersion": 1, "scope": "local-isolated-T12", "run": self.run,
                         "startedAt": now(), "githubActions": False, "azureDeployment": False,
                         "liveFoundry": False, "clockManipulated": False, "status": "blocked", "checks": {}}
        self.evidence["harnessSha256"] = {path.name: hashlib.sha256(path.read_bytes()).hexdigest()
            for path in (Path(__file__), Path(__file__).with_name("local-runtime-driver.mjs"))}

    def command(self, args, *, data=None, timeout=60, env=None, allow_failure=False):
        result = subprocess.run(args, input=data, capture_output=True, text=True, encoding="utf-8",
                                errors="replace", cwd=ROOT, timeout=timeout, env=env)
        if result.returncode and not allow_failure:
            if self.private.exists():
                (self.private / "last-command.log").write_text(result.stdout + result.stderr, encoding="utf-8")
            self.last_diagnostic = self.diagnostics(result.stdout + result.stderr)
            raise RuntimeFailure(f"{self.phase}: {args[0]} failed (exit {result.returncode}); raw output withheld.")
        return result

    def diagnostics(self, text):
        messages = []
        if "failed to connect to the docker API" in text:
            messages.append("Docker API unavailable; no daemon or context configuration was changed.")
        if "less than or equal to 8000" in text:
            messages.append("sqlcmd rejected an out-of-range display width (maximum 8000).")
        if "Webhooks are not configured" in text:
            messages.append("Durable webhooks are not configured (local WEBSITE_HOSTNAME required).")
        if "Cohors workload API rejected the operation" in text:
            messages.append("Workload API rejected a real Functions request; inspect filtered HTTP status counts.")
        for kind in sorted(set(re.findall(r"Workflow readiness failed: ([A-Za-z0-9_]+)", text))):
            messages.append(f"Workflow readiness failed ({kind}).")
        for kind in sorted(set(re.findall(r"\b((?:System|Grpc|Azure|Microsoft)\.[A-Za-z0-9_.]+Exception)\b", text))):
            messages.append(f"Exception type: {kind}")
        for code in sorted(set(re.findall(r'StatusCode\s*=\s*["\']?([A-Za-z]+)', text))):
            messages.append(f"RPC/HTTP status: {code}")
        for line in text.splitlines():
            if re.search(r"Local runtime action failed:|error (?:CS|NU|MSB|NETSDK)\d+|Error response from daemon:|"
                         r"Sqlcmd: Error:|"
                         r"sqlservr:|Error: \d+,|Permission denied|PAL initialization|"
                         r"This program has encountered|SQL Server.*(?:failed|requires|cannot|unable)", line):
                for secret in self.prohibited:
                    line = line.replace(secret, "[REDACTED]")
                if not re.search(r"(?i)Authorization|Cookie|access_token|refresh_token|password=|[?&](?:code|sig)=", line):
                    messages.append(line[:400])
        return messages[-8:]

    def docker(self, *args, **kwargs):
        return self.command(["docker", *args], **kwargs).stdout.strip()

    def stage(self, phase):
        self.phase = phase
        print(f"T12 local: {phase}", flush=True)

    def inspect(self, role):
        return json.loads(self.docker("container", "inspect", self.containers[role],
                                     "--format", "{{json .State}}"))

    def wait(self, title, probe, predicate, seconds=120, interval=2):
        end = time.monotonic() + seconds
        while True:
            value = probe()
            if predicate(value):
                return value
            require(time.monotonic() < end, f"Timed out waiting for {title}; no success inferred.")
            time.sleep(interval)

    def driver(self, action, **kwargs):
        result = self.docker("exec", "-i", self.containers["driver"], "node", "/t12/driver.mjs", "client",
                             data=json.dumps({"action": action, **kwargs}), timeout=60)
        return json.loads(result)

    def sql(self, query, database="CohorsT12"):
        env = os.environ | {"SQLCMDPASSWORD": self.sql_password}
        text = self.docker("exec", "-i", "--env", "SQLCMDPASSWORD", self.containers["sql"],
                           "/opt/mssql-tools18/bin/sqlcmd", "-S", "127.0.0.1", "-U", "sa",
                           "-d", database, "-C", "-b", "-l", "5", "-h", "-1", "-w", "65535", "-y", "8000",
                           "-i", "/dev/stdin", data="SET NOCOUNT ON;\n" + query + "\nGO\n", env=env)
        return text

    def heartbeat(self):
        return json.loads(self.sql("SELECT Json FROM dbo.Aggregates WHERE Kind='operations' AND Id='heartbeat';") or "null")

    def business(self, case_id, event_id):
        require(re.fullmatch(r"[a-f0-9]{32}", case_id) and re.fullmatch(r"[a-f0-9]{32}", event_id), "Invalid observed SQL identifiers.")
        query = f"""SELECT
 (SELECT COUNT(*) FROM dbo.Inbox WHERE EventId='{event_id}' AND Consumer='cohors-domain-events-v1') receipts,
 (SELECT COUNT(*) FROM dbo.AuditEvents WHERE AggregateId='{case_id}' AND Action='AssessmentDrafted') drafts,
 (SELECT COUNT(*) FROM dbo.AuditEvents WHERE AggregateId='{case_id}' AND Action='AgentStarted') starts,
 (SELECT COUNT(*) FROM dbo.Aggregates a CROSS APPLY OPENJSON(a.Json,'$.assessments') j WHERE a.Kind='case' AND a.Id='{case_id}') assessments,
 (SELECT Attempts FROM dbo.Outbox WHERE Id='{event_id}') attempts,
 CAST((SELECT CASE WHEN CompletedAt IS NOT NULL THEN 1 ELSE 0 END FROM dbo.Outbox WHERE Id='{event_id}') AS bit) completed,
 JSON_QUERY((SELECT a.Id id, JSON_VALUE(a.Json,'$.status') status, JSON_VALUE(a.Json,'$.inputHash') inputHash,
 JSON_VALUE(parts.inputJson,'$.mode') mode, JSON_VALUE(parts.inputJson,'$.role') role,
 JSON_VALUE(parts.outputJson,'$.model') model
 FROM dbo.Aggregates a CROSS APPLY OPENJSON(a.Json) WITH(inputJson nvarchar(max),outputJson nvarchar(max)) parts
 WHERE a.Kind='agentInvocation' AND JSON_VALUE(parts.inputJson,'$.case.id')='{case_id}' ORDER BY a.Id FOR JSON PATH)) invocations
FOR JSON PATH, WITHOUT_ARRAY_WRAPPER;"""
        return json.loads(self.sql(query))

    def env_file(self, role, values):
        require(all("\n" not in str(value) and "\r" not in str(value) for value in values.values()), "Multiline container configuration refused.")
        path = self.private / f"{role}.env"
        path.write_text("".join(f"{key}={value}\n" for key, value in values.items()), encoding="utf-8")
        path.chmod(0o600)
        return path

    def volume(self, role):
        name = f"{self.prefix}-{role}"
        require(not self.docker("volume", "ls", "--filter", f"name=^{name}$", "--format", "{{.Name}}"),
                "Refusing to reuse an existing runtime volume.")
        self.docker("volume", "create", "--label", f"{LABEL}={self.run}", name)
        self.volumes.append(name)
        return name

    def container(self, role, image, options=(), command=(), *, memory="512m", start=True):
        name = f"{self.prefix}-{role}"
        require(role not in self.containers, "Runtime role already created.")
        network = "none" if role == "sql" else f"container:{self.containers['sql']}"
        container_id = self.docker("container", "create", "--name", name, "--label", f"{LABEL}={self.run}",
                                   "--network", network, "--memory", memory, "--cpus", "2", "--pids-limit", "512",
                                   "--security-opt", "no-new-privileges", "--cap-drop", "ALL",
                                   "--log-opt", "max-size=2m", "--log-opt", "max-file=2",
                                   *options, image, *command)
        require(re.fullmatch(r"[a-f0-9]{64}", container_id), "Missing exact owned container identity.")
        self.containers[role] = container_id
        if start:
            self.docker("container", "start", container_id)
        return container_id

    def ready(self, role, seconds=120):
        def probe():
            require(self.inspect(role)["Running"], f"{role} exited before readiness.")
            result = self.driver("probe", role=role)
            self.evidence.setdefault("readiness", {})[role] = {"at": now(), **result}
            return result
        return self.wait(f"{role} real host readiness", probe, lambda value: value == {"code": 200, "status": "ready"}, seconds)

    def prepare(self):
        require(shutil.which("docker") and shutil.which("git"), "Normal Docker Linux and git are required.")
        identity = self.command(["git", "show", "--no-patch", "--format=%H%n%P", SOURCE_SHA]).stdout.splitlines()
        require(identity == [SOURCE_SHA, SOURCE_PARENT], "The authorized source commit/parent does not match.")
        tree = self.command(["git", "rev-parse", SOURCE_SHA + "^{tree}"]).stdout.strip()
        tracked = self.command(["git", "ls-tree", "-r", "--name-only", SOURCE_SHA]).stdout.splitlines()
        require(not any(".local" in Path(name).parts or name.endswith("demo-access.txt") for name in tracked),
                "Tracked local credentials are not an allowed export.")
        self.evidence["source"] = {"commit": SOURCE_SHA, "parent": SOURCE_PARENT, "tree": tree,
                                  "trackedFiles": len(tracked), "exportCreated": False}
        info = json.loads(self.docker("info", "--format", "{{json .}}"))
        require(info["OSType"] == "linux", "Linux containers are required; no Docker setting is changed.")
        stats = self.docker("stats", "--no-stream", "--format", "{{json .}}")
        used = sum(memory_bytes(json.loads(line)["MemUsage"].split("/")[0]) for line in stats.splitlines())
        require(info["MemTotal"] - used >= 8 * 1024**3, "Less than 8 GiB Docker headroom; wait for other work, do not stop it.")
        require(shutil.disk_usage(self.workspace.parent).free >= 15 * 1024**3, "Insufficient local disk headroom.")
        self.private.mkdir(parents=True)
        self.source.mkdir()
        (self.workspace / "owner.json").write_text(json.dumps({"run": self.run, "scope": "local-T12"}), encoding="utf-8")
        archive = self.workspace / "source.tar"
        self.command(["git", "archive", "--format=tar", f"--output={archive}", SOURCE_SHA])
        with tarfile.open(archive) as contents:
            contents.extractall(self.source, filter="data")
        self.evidence["source"].update({"archiveSha256": hashlib.sha256(archive.read_bytes()).hexdigest(),
                                       "exportCreated": True, "applicationFilesModified": False})
        self.source_hashes = {name: hashlib.sha256((self.source / name).read_bytes()).hexdigest() for name in tracked}
        self.evidence["capacity"] = {"dockerMemoryBytes": info["MemTotal"], "observedUsedBytes": int(used),
                                     "cpus": info["NCPU"], "publishedHostPorts": [], "network": "owned shared loopback; none"}
        for role, image in BASE_IMAGES.items():
            found = self.command(["docker", "image", "inspect", image, "--format", "{{.Id}}"], allow_failure=True)
            require(found.returncode == 0, f"Missing local image {image}; supply it with the normal Docker pull command, then use a new run directory.")
            self.images[role] = found.stdout.strip()
        self.stage("build-four-authorized-host-images")
        for role, project in HOSTS.items():
            tag = f"{self.prefix}-{role}:local"
            self.tags.append(tag)
            self.docker("build", "--quiet", "--label", f"{LABEL}={self.run}",
                        "--file", str(self.source / "src" / f"Cohors.{project}" / "Dockerfile"),
                        "--tag", tag, str(self.source), timeout=900)
            self.images[role] = self.docker("image", "inspect", tag, "--format", "{{.Id}}")
        self.evidence["images"] = dict(self.images)

    def launch(self):
        self.stage("fresh-isolated-sql-and-azurite")
        self.sql_password = "T12!" + secrets.token_urlsafe(32)
        workload_key, agent_key, password, control_key = (secrets.token_hex(32) for _ in range(4))
        storage_keys = [base64.b64encode(secrets.token_bytes(32)).decode() for _ in range(2)]
        salt = secrets.token_bytes(16)
        password_hash = base64.b64encode(hashlib.pbkdf2_hmac("sha256", password.encode(), salt, 210000, 32)).decode()
        self.prohibited = [self.sql_password, workload_key, agent_key, password, control_key, password_hash,
                           base64.b64encode(salt).decode(), *storage_keys]
        sql_volume, azurite_volume, api_volume = (self.volume(role) for role in ("sql", "azurite", "api"))
        sql_env = self.env_file("sql", {"ACCEPT_EULA": "Y", "MSSQL_PID": "Developer",
            "MSSQL_SA_PASSWORD": self.sql_password, "MSSQL_MEMORY_LIMIT_MB": "2048"})
        # The stock sqlservr binary declares cap_net_bind_service=ep.
        self.container("sql", self.images["sql"], ["--cap-add", "NET_BIND_SERVICE",
            "--env-file", str(sql_env), "--mount", f"type=volume,src={sql_volume},dst=/var/opt/mssql"], memory="3g")
        self.evidence["sqlFileCapabilities"] = self.docker("exec", self.containers["sql"],
            "/usr/sbin/getcap", "/opt/mssql/bin/sqlservr")
        require(self.evidence["sqlFileCapabilities"] == "/opt/mssql/bin/sqlservr cap_net_bind_service=ep",
                "Unexpected SQL binary capabilities; no additional permission is granted.")
        sql_ready = False
        for _ in range(60):
            require(self.inspect("sql")["Running"], "Own SQL container exited.")
            result = self.command(["docker", "exec", "--env", "SQLCMDPASSWORD", self.containers["sql"],
                "/opt/mssql-tools18/bin/sqlcmd", "-S", "127.0.0.1", "-U", "sa", "-C", "-b", "-l", "2",
                "-Q", "SELECT 1"], env=os.environ | {"SQLCMDPASSWORD": self.sql_password}, allow_failure=True)
            if result.returncode == 0:
                sql_ready = True
                break
            time.sleep(2)
        require(sql_ready, "Own SQL did not become ready.")
        self.sql("CREATE DATABASE CohorsT12;", database="master")
        azurite_env = self.env_file("azurite", {"AZURITE_ACCOUNTS": f"t12workflows:{storage_keys[0]};t12operations:{storage_keys[1]}"})
        self.container("azurite", self.images["azurite"], ["--env-file", str(azurite_env),
            "--mount", f"type=volume,src={azurite_volume},dst=/data"],
            ["azurite", "--location", "/data", "--blobHost", "127.0.0.1", "--queueHost", "127.0.0.1", "--tableHost", "127.0.0.1", "--silent"])
        initializer = self.container("state-init", self.images["driver"],
            ["--cap-add", "CHOWN", "--mount", f"type=volume,src={api_volume},dst=/state"],
            ["node", "-e", "require('node:fs').chownSync('/state',1654,1654)"])
        require(self.docker("container", "wait", initializer) == "0", "Owned object-state permission setup failed.")
        connection = f"Server=127.0.0.1,1433;Database=CohorsT12;User ID=sa;Password={self.sql_password};Encrypt=true;TrustServerCertificate=true"
        api_env = self.env_file("api", {"ASPNETCORE_ENVIRONMENT": "Development", "ASPNETCORE_URLS": "http://127.0.0.1:8080",
            "Cohors__SqlAuthentication": "Local", "ConnectionStrings__Cohors": connection,
            "Cohors__ObjectStore": "Local", "Cohors__Search": "Local", "Cohors__LocalOperations": "false",
            "Cohors__AutoMigrate": "true", "Cohors__Seed": "true", "Cohors__PublicSources": "false",
            "Authentication__Mode": "LocalDemo", "Authentication__WorkloadKey": workload_key,
            "Authentication__Users__0__Id": self.run, "Authentication__Users__0__Username": "t12.pms",
            "Authentication__Users__0__Name": "Synthetic T12 PMS", "Authentication__Users__0__Role": "PmsSpecialist",
            "Authentication__Users__0__Products__0": "ortho", "Authentication__Users__0__Products__1": "cardio",
            "Authentication__Users__0__Salt": base64.b64encode(salt).decode(), "Authentication__Users__0__Hash": password_hash,
            "Cohors__AgentTransport": "Remote", "Agents__Endpoint": "http://127.0.0.1:8086",
            "Agents__LocalWorkloadKey": agent_key})
        agents_env = self.env_file("agents", {"ASPNETCORE_ENVIRONMENT": "Development",
            "ASPNETCORE_URLS": "http://127.0.0.1:8081", "Cohors__LocalWorkloadKey": agent_key,
            "Agents__Products__0": "ortho", "Agents__Products__1": "cardio"})
        hub = "CohorsT12" + self.run[:12]
        driver_config = self.private / "driver.json"
        driver_config.write_text(json.dumps({"run": self.run, "username": "t12.pms", "password": password,
            "controlKey": control_key, "storageAccount": "t12workflows", "storageKey": storage_keys[0], "hub": hub}), encoding="utf-8")
        driver_config.chmod(0o600)
        self.container("driver", self.images["driver"], ["--mount", f"type=bind,src={driver_config},dst=/t12/private.json,readonly",
            "--mount", f"type=bind,src={Path(__file__).with_name('local-runtime-driver.mjs')},dst=/t12/driver.mjs,readonly"],
            ["node", "/t12/driver.mjs", "serve"], memory="256m")
        self.container("agents", self.images["agents"], ["--env-file", str(agents_env)], memory="768m")
        self.container("api", self.images["api"], ["--env-file", str(api_env), "--mount", f"type=volume,src={api_volume},dst=/app/.local"], memory="1g")
        self.ready("api", 180)
        self.ready("agents")
        status = self.driver("status")
        require(status["heartbeat"] is None and status["heartbeatMissing"] is True and
                status["orchestration"] == "Durable Functions" and status["agentRuntime"] == "Remote" and
                status["foundryConfigured"] is False, "The local host profile or never-started heartbeat is wrong.")
        self.evidence["checks"]["neverStartedOperations"] = status
        for index, role in enumerate(("workflows", "operations")):
            account = "t12" + role
            storage = (f"DefaultEndpointsProtocol=http;AccountName={account};AccountKey={storage_keys[index]};"
                f"BlobEndpoint=http://127.0.0.1:10000/{account};QueueEndpoint=http://127.0.0.1:10001/{account};"
                f"TableEndpoint=http://127.0.0.1:10002/{account};")
            environment = {"AzureWebJobsStorage": storage, "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
                "FUNCTIONS_EXTENSION_VERSION": "~4", "AZURE_FUNCTIONS_ENVIRONMENT": "Development",
                "DOTNET_ENVIRONMENT": "Development", "ASPNETCORE_ENVIRONMENT": "Development",
                "ASPNETCORE_URLS": f"http://127.0.0.1:{8082 + index}",
                "WEBSITE_HOSTNAME": f"127.0.0.1:{8082 + index}", "AzureWebJobsSecretStorageType": "files",
                "AzureFunctionsJobHost__logging__logLevel__Host.Executor": "Information"}
            if role == "workflows":
                environment.update({"Cohors__ApiBaseUrl": "http://127.0.0.1:8084",
                    "Cohors__LocalWorkloadKey": workload_key, "AzureFunctionsJobHost__extensions__durableTask__hubName": hub})
            else:
                environment.update({"Cohors__SqlAuthentication": "Local", "ConnectionStrings__Cohors": connection})
            path = self.env_file(role, environment)
            self.container(role, self.images[role], ["--env-file", str(path)], memory="1280m", start=False)
        self.evidence["hostIdentities"] = {role: {"containerId": self.containers[role], "imageId": self.images[role]} for role in HOSTS}

    def execute(self):
        self.stage("real-operations-timer-and-workflow-dispatch")
        self.docker("start", self.containers["operations"])
        self.ready("operations", 180)
        first = self.heartbeat()
        second = self.wait("two real Operations timer ticks", self.heartbeat, lambda hb: hb and hb["version"] >= first["version"] + 1, 90)
        self.evidence["checks"]["operationsTimerHeartbeats"] = {"first": first, "second": second}
        self.docker("start", self.containers["workflows"])
        self.ready("workflows", 180)
        running_hosts = {role: self.inspect(role) for role in HOSTS}
        require(all(state["Running"] for state in running_hosts.values()) and
                len({state["Pid"] for state in running_hosts.values()}) == len(HOSTS),
                "All four independent host processes must actually be running.")
        for role, state in running_hosts.items():
            self.evidence["hostIdentities"][role].update({"pid": state["Pid"], "startedAt": state["StartedAt"]})
        self.wait("initial real outbox drain", lambda: int(self.sql("SELECT COUNT(*) FROM dbo.Outbox WHERE CompletedAt IS NULL;")),
                  lambda count: count <= 2, 180)
        self.stage("real-replay-effect-held-before-durable-ack")
        requested = self.driver("request-assessment")
        require(requested["mode"] == "Replay" and requested["status"] == "Queued", "Replay was not durably queued.")
        transport_before = self.wait("real committed response at transport barrier", lambda: self.driver("transport"),
                                     lambda value: value["held"], 180)
        before = self.business(requested["caseId"], transport_before["eventId"])
        verify_business(before)
        durable_before = self.driver("durable")
        require(len(durable_before["instances"]) == 1 and durable_before["instances"][0]["RuntimeStatus"] == "Running",
                "No persisted running Durable instance before interruption.")
        require(any(item.get("EventType") == "TaskScheduled" for item in durable_before["history"]),
                "No persisted real activity schedule before interruption.")
        self.evidence["checks"]["workflowRestart"] = {"before": before, "transportBefore": transport_before,
                                                      "durableBefore": durable_before, "recoveryVerified": False}
        heartbeat_before = self.heartbeat()
        process_before = self.inspect("workflows")
        self.docker("kill", "--signal", "KILL", self.containers["workflows"])
        require(not self.inspect("workflows")["Running"], "Own Workflows host was not interrupted.")
        self.stage("operations-continues-while-workflows-is-down")
        heartbeat_during = self.wait("independent Operations ticks while Workflows is down", self.heartbeat,
                                    lambda hb: hb["version"] >= heartbeat_before["version"] + 2, 100)
        require(self.driver("durable")["instances"][0]["RuntimeStatus"] == "Running", "Interrupted work did not remain persisted.")
        self.driver("release")
        self.docker("start", self.containers["workflows"])
        self.ready("workflows", 180)
        self.stage("same-durable-instance-redelivers-without-duplicate-effect")
        durable_after = self.wait("real Durable recovery after storage visibility timeout", lambda: self.driver("durable"),
                                  lambda value: len(value["instances"]) == 1 and value["instances"][0]["RuntimeStatus"] == "Completed",
                                  540, 5)
        after = self.business(requested["caseId"], transport_before["eventId"])
        transport_after = self.driver("transport")
        verify_resume(before, after, transport_before, transport_after)
        require(durable_before["instances"][0]["ExecutionId"] == durable_after["instances"][0]["ExecutionId"],
                "Recovery used a different Durable execution.")
        require(sum(item.get("EventType") == "TaskCompleted" for item in durable_after["history"]) == 1,
                "Expected one recorded activity completion.")
        self.evidence["checks"]["workflowRestart"].update({"after": after, "transportAfter": transport_after,
            "durableAfter": durable_after, "recoveryVerified": True,
            "hostBefore": {"pid": process_before["Pid"], "startedAt": process_before["StartedAt"]},
            "hostAfter": {key: self.inspect("workflows")[key] for key in ("Pid", "StartedAt")},
            "operationsBefore": heartbeat_before, "operationsDuringOutage": heartbeat_during})
        self.stage("operations-remains-independent-of-api-and-agenthost")
        independent_before = self.heartbeat()
        require(self.inspect("api")["Running"] and self.inspect("agents")["Running"],
                "API and AgentHost must run before their controlled independent stop.")
        self.docker("stop", "--time", "10", self.containers["api"], self.containers["agents"], timeout=40)
        independent_after = self.wait("Operations tick with API and AgentHost stopped", self.heartbeat,
                                     lambda hb: hb["version"] > independent_before["version"], 75)
        require(not self.inspect("api")["Running"] and not self.inspect("agents")["Running"], "Host independence was not exercised.")
        self.evidence["checks"]["independentOperations"] = {"before": independent_before, "after": independent_after}
        self.docker("start", self.containers["agents"], self.containers["api"])
        self.ready("api", 180)
        self.ready("agents")
        self.stage("real-three-minute-missing-heartbeat-and-operations-restart")
        operations_before = self.inspect("operations")
        self.docker("stop", "--time", "10", self.containers["operations"], timeout=30)
        require(not self.inspect("operations")["Running"], "Own Operations host did not stop.")
        heartbeat_at_stop = self.heartbeat()
        samples = []
        def missing():
            started = datetime.now(timezone.utc)
            status = self.driver("status")
            require(status["heartbeat"]["lastTick"] == heartbeat_at_stop["lastTick"], "Stopped Operations heartbeat changed.")
            tick = datetime.fromisoformat(status["heartbeat"]["lastTick"])
            lower = (started - tick).total_seconds()
            upper = (datetime.now(timezone.utc) - tick).total_seconds()
            flag = status["heartbeatMissing"]
            require((upper > 180 or not flag) and (lower <= 180 or flag),
                    "Real API heartbeat age and missing flag disagree.")
            samples.append({"at": now(), "ageLowerSeconds": round(lower, 3),
                            "ageUpperSeconds": round(upper, 3), "missing": flag})
            return flag and lower > 180
        self.wait("real, unmodified three-minute heartbeat expiry", missing, bool, 210, 3)
        require(any(not sample["missing"] and sample["ageUpperSeconds"] <= 180 for sample in samples),
                "No healthy heartbeat observation before the real missing threshold.")
        self.docker("start", self.containers["operations"])
        self.ready("operations", 180)
        heartbeat_after = self.heartbeat()
        require(heartbeat_after["version"] > heartbeat_at_stop["version"] and not self.driver("status")["heartbeatMissing"],
                "Separate Operations restart did not restore a genuine fresh heartbeat.")
        operations_after = self.inspect("operations")
        require(operations_before["StartedAt"] != operations_after["StartedAt"], "Operations did not start a new process.")
        self.evidence["checks"]["operationsRestart"] = {"before": heartbeat_at_stop, "samples": samples, "after": heartbeat_after,
            "hostBefore": {key: operations_before[key] for key in ("Pid", "StartedAt")},
            "hostAfter": {key: operations_after[key] for key in ("Pid", "StartedAt")}}
        timer_evidence = {}
        for role, function in (("workflows", "DispatchOutbox"), ("operations", "DeadlineWatchdog")):
            logs = self.docker("logs", self.containers[role])
            executions = len(re.findall(rf"Executing 'Functions\.{function}'.*Timer fired", logs))
            completions = len(re.findall(rf"Executed 'Functions\.{function}' \(Succeeded", logs))
            require(executions > 0 and completions > 0, f"No genuine {function} Timer execution/completion log evidence.")
            require(not self.inspect(role)["OOMKilled"], f"{role} was memory terminated.")
            timer_evidence[function] = {"timerExecutions": executions, "successfulExecutions": completions}
        self.evidence["checks"]["realTimerLogs"] = timer_evidence
        require(all(hashlib.sha256((self.source / name).read_bytes()).hexdigest() == digest
                    for name, digest in self.source_hashes.items()), "Exported application source changed during the test.")
        self.evidence["status"] = "passed"

    def cleanup(self):
        failures = []
        for role, container_id in reversed(list(self.containers.items())):
            identity = self.command(["docker", "container", "inspect", container_id, "--format",
                '{{index .Config.Labels "' + LABEL + '"}}'], allow_failure=True)
            if identity.returncode or identity.stdout.strip() != self.run:
                failures.append(f"container ownership: {role}")
                continue
            if self.command(["docker", "container", "rm", "--force", container_id], allow_failure=True).returncode:
                failures.append(f"container removal: {role}")
        for volume in reversed(self.volumes):
            identity = self.command(["docker", "volume", "inspect", volume, "--format",
                '{{index .Labels "' + LABEL + '"}}'], allow_failure=True)
            if identity.returncode or identity.stdout.strip() != self.run:
                failures.append("volume ownership")
            elif self.command(["docker", "volume", "rm", volume], allow_failure=True).returncode:
                failures.append("owned volume removal")
        for tag in self.tags:
            identity = self.command(["docker", "image", "inspect", tag, "--format",
                '{{index .Config.Labels "' + LABEL + '"}}'], allow_failure=True)
            if identity.returncode:
                if "No such image" not in identity.stderr:
                    failures.append("image tag state unavailable")
                continue
            if identity.stdout.strip() != self.run:
                failures.append("image tag ownership")
            elif self.command(["docker", "image", "rm", tag], allow_failure=True).returncode:
                failures.append("owned image tag removal")
        if not failures and self.workspace.exists():
            owner = json.loads((self.workspace / "owner.json").read_text(encoding="utf-8"))
            require(owner == {"run": self.run, "scope": "local-T12"}, "Runtime workspace ownership mismatch.")
            for directory in (self.private, self.source):
                if directory.exists():
                    require(directory.parent == self.workspace and not directory.is_symlink(), "Unsafe disposable cleanup path.")
                    shutil.rmtree(directory)
            archive = self.workspace / "source.tar"
            if archive.exists():
                require(not archive.is_symlink(), "Unsafe source archive cleanup path.")
                archive.unlink()
        self.evidence["cleanup"] = {"complete": not failures, "failures": failures,
                                    "ownedContainers": len(self.containers), "ownedVolumes": len(self.volumes),
                                    "privateMaterialRemoved": not self.private.exists(),
                                    "foreignResourcesTouched": False}
        if failures:
            self.evidence["status"] = "blocked"

    def run_all(self):
        try:
            self.prepare()
            self.launch()
            self.execute()
        except (RuntimeFailure, subprocess.TimeoutExpired, OSError, ValueError, KeyError) as error:
            self.evidence["failure"] = {"phase": self.phase, "type": type(error).__name__,
                                        "message": str(error) if isinstance(error, RuntimeFailure) else "Local contract execution failed; raw details withheld.",
                                        "diagnostics": self.last_diagnostic}
            for role, container_id in self.containers.items():
                logs = self.command(["docker", "logs", "--tail", "80", container_id], allow_failure=True)
                state = self.command(["docker", "container", "inspect", container_id, "--format", "{{json .State}}"], allow_failure=True)
                self.evidence["failure"][role] = {"state": json.loads(state.stdout) if state.returncode == 0 else {"unavailable": True},
                    "diagnostics": self.diagnostics(logs.stdout + logs.stderr)}
            if self.evidence["failure"].get("driver", {}).get("state", {}).get("Running"):
                transport = self.command(["docker", "exec", "-i", self.containers["driver"],
                    "node", "/t12/driver.mjs", "client"], data='{"action":"transport"}', allow_failure=True)
                if transport.returncode == 0:
                    self.evidence["failure"]["transport"] = json.loads(transport.stdout)
                else:
                    self.evidence["failure"]["transportUnavailable"] = True
        finally:
            self.cleanup()
            self.evidence["finishedAt"] = now()
            self.artifacts.mkdir(parents=True, exist_ok=False)
            (self.artifacts / "t12-runtime-results.json").write_text(filtered_json(self.evidence, self.prohibited), encoding="utf-8")
        print(f"T12 local result: {self.evidence['status']}; Azure/Actions/Checkov gates unchanged.", flush=True)
        return 0 if self.evidence["status"] == "passed" else 1


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--workspace", required=True, type=Path, help="New disposable directory outside the repository.")
    parser.add_argument("--artifacts", required=True, type=Path, help="New separate directory for filtered evidence only.")
    args = parser.parse_args()
    try:
        sys.exit(Harness(args.workspace, args.artifacts).run_all())
    except RuntimeFailure as error:
        parser.exit(1, f"Local T12 refused: {error}\n")
