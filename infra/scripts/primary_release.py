"""Owned private execution, explicit reconciliation, activation, stop and cleanup."""

import base64
import json
import os
from pathlib import Path
import re
import subprocess
import time
import urllib.parse
import urllib.request
import urllib.error

from primary_control import ARM, GRAPH, GateError, NoRedirect, canonical, digest, read_json, file_hash, timestamp


ROOT = Path(__file__).resolve().parents[2]
COMPUTE_VERSION = "2024-11-01"


def github_json(path):
    result = subprocess.run(["gh", "api", path], stdin=subprocess.DEVNULL, capture_output=True,
                            timeout=90, check=False)
    if result.returncode:
        raise GateError("Existing GitHub context cannot read the bound package; no login or token fallback.")
    return json.loads(result.stdout)


def artifact_url(binding):
    repo = binding["repository"]
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repo):
        raise GateError("Invalid bound GitHub repository.")
    artifact = github_json(f"repos/{repo}/actions/artifacts/{int(binding['artifactId'])}")
    if (artifact.get("expired") or artifact.get("digest") != binding["artifactDigest"]
            or artifact.get("workflow_run", {}).get("id") != binding["runId"]
            or artifact.get("workflow_run", {}).get("head_sha") != binding["infrastructureCommit"]):
        raise GateError("The exact built artifact expired or its digest/run/source binding differs.")
    run = github_json(f"repos/{repo}/actions/runs/{binding['runId']}")
    if (run.get("status") != "completed" or run.get("conclusion") != "success"
            or run.get("head_sha") != binding["infrastructureCommit"]
            or run.get("path") != ".github/workflows/cohors-primary-package.yml"
            or run.get("repository", {}).get("full_name") != repo
            or run.get("event") not in ("push", "workflow_dispatch")):
        raise GateError("A failed, foreign or unrecognized workflow cannot supply a deployable package.")
    result = subprocess.run(["gh", "auth", "token", "--hostname", "github.com"],
                            stdin=subprocess.DEVNULL, capture_output=True, timeout=90, check=False)
    if result.returncode:
        raise GateError("The existing GitHub authentication context is unavailable; no sign-in was attempted.")
    token = result.stdout.decode().strip()
    request = urllib.request.Request(f"https://api.github.com/repos/{repo}/actions/artifacts/{artifact['id']}/zip",
                                     headers={"Authorization": "Bearer " + token,
                                              "Accept": "application/vnd.github+json"})
    try:
        urllib.request.build_opener(NoRedirect()).open(request, timeout=60).close()
    except urllib.error.HTTPError as error:
        if error.code != 302:
            raise GateError(f"GitHub artifact access returned HTTP {error.code}; no alternative identity or route.") from error
        url = error.headers.get("Location", "")
        error.close()
    else:
        raise GateError("Expected one protected artifact redirect; no user credential may go to the runner.")
    parsed = urllib.parse.urlsplit(url)
    if (parsed.scheme != "https" or not parsed.hostname.endswith(".blob.core.windows.net")
            or parsed.username or parsed.fragment):
        raise GateError("Artifact delivery host is outside the reviewed GitHub Azure-blob transport.")
    return url


def nva_policy(manifest, platform=None):
    policy = dict(manifest["nvaPolicy"])
    policy.update(contract="cohors-nva-policy-1", expiresAtUtc=manifest["expiresAtUtc"],
                  bindingHash=manifest["bindingHash"], sourceNetwork=manifest["network"]["vnet"],
                  listenAddress=manifest["network"]["nvaPrivateIp"])
    private = []
    if platform:
        for key, value in platform["endpoints"].items():
            if key in ("sqlServer", "registry"):
                private.append(value)
            else:
                private.append(urllib.parse.urlsplit(value).hostname)
        private.extend(urllib.parse.urlsplit(value).hostname for value in platform.get("runtimeUrls", {}).values())
    policy["privateOnlyHosts"] = sorted(set(private))
    return policy


def host_request(engine, phase, *, vm=None):
    manifest, outputs = engine.manifest, engine.outputs
    network = outputs["phase-network"]
    stage = outputs.get("phase-runtime") or outputs.get("phase-foundation") or {}
    request = {"contract": "cohors-primary-private-request-1", "bindingHash": manifest["bindingHash"],
               "phase": phase, "vmId": vm or network["access"]["operatorVmId"],
               "tenantId": manifest["binding"]["tenantId"], "sourceNetwork": manifest["network"]["vnet"],
               "applicationCommit": manifest["releaseBundle"]["applicationCommit"],
               "infrastructureCommit": manifest["releaseBundle"]["infrastructureCommit"],
               "platform": stage.get("platform"), "identities": network["access"]["identities"],
               "directory": outputs["phase-identities"],
               "runtimeIdentities": outputs.get("phase-grants", {}).get("runtimeIdentities", {}),
               "model": manifest["model"], "boundaryReview": manifest["boundaryReview"],
               "nvaPolicy": nva_policy(manifest, stage.get("platform"))}
    return request


def bootstrap_script(binding):
    # Only the read-only, short-lived artifact URL is protected. No Azure user token goes to a VM.
    return """#!/bin/bash
set -eu
umask 077
python3 - <<'PY'
import base64, hashlib, json, os, pathlib, stat, subprocess, tempfile, urllib.error, urllib.request, zipfile
from pathlib import PurePosixPath
class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs): return None
def unpack(source, destination):
    with zipfile.ZipFile(source) as archive:
        if sum(item.file_size for item in archive.infolist()) > 12000000000: raise ValueError('archive-size')
        for item in archive.infolist():
            p=PurePosixPath(item.filename)
            if p.is_absolute() or '..' in p.parts or '\\\\' in item.filename or ':' in item.filename or stat.S_ISLNK(item.external_attr >> 16):
                raise ValueError('unsafe-archive')
        archive.extractall(destination)
request=json.loads(base64.b64decode(os.environ.pop('COHORS_REQUEST')))
url=os.environ.pop('COHORS_ARTIFACT_URI')
base=pathlib.Path('/var/lib/cohors-primary')
base.mkdir(mode=0o700,parents=True,exist_ok=True)
with tempfile.TemporaryDirectory(prefix='package-',dir=base) as temp:
    temp=pathlib.Path(temp); archive=temp/'artifact.zip'
    try:
        with urllib.request.build_opener(NoRedirect(),urllib.request.ProxyHandler({})).open(url,timeout=120) as response, archive.open('xb') as output:
            total=0
            while block:=response.read(1048576):
                total+=len(block)
                if total>4000000000: raise ValueError('download-size')
                output.write(block)
    except (OSError,urllib.error.URLError,TimeoutError):
        raise SystemExit('COHORS_GATE: artifact transport failed; URL withheld')
    with archive.open('rb') as stream: actual=hashlib.file_digest(stream,'sha256').hexdigest()
    if actual!='ARTIFACT_SHA': raise SystemExit('COHORS_GATE: artifact digest mismatch')
    unpack(archive,temp/'outer')
    bundle=temp/'outer/cohors-primary-release.zip'
    with bundle.open('rb') as stream: actual=hashlib.file_digest(stream,'sha256').hexdigest()
    if actual!='BUNDLE_SHA': raise SystemExit('COHORS_GATE: bundle digest mismatch')
    unpack(bundle,temp/'bundle')
    request_file=temp/'request.json'; request_file.write_text(json.dumps(request),encoding='utf-8')
    subprocess.run(['python3','-B',str(temp/'bundle/infra/scripts/primary_host.py'),'--bundle',str(temp/'bundle'),'--request',str(request_file)],check=True)
PY
""".replace("ARTIFACT_SHA", binding["artifactDigest"].removeprefix("sha256:")).replace("BUNDLE_SHA", binding["bundleSha256"])


def receipt_from_command(value, request):
    view = value.get("properties", {}).get("instanceView", {})
    if view.get("executionState") != "Succeeded" or view.get("exitCode") != 0:
        raise GateError("Managed Run Command has no successful private execution result; no output guessing.")
    lines = [line.removeprefix("COHORS_RECEIPT:") for line in view.get("output", "").splitlines()
             if line.startswith("COHORS_RECEIPT:")]
    if len(lines) != 1:
        raise GateError("Missing or truncated private receipt; do not infer success from provisioningState.")
    try:
        receipt = json.loads(base64.b64decode(lines[0], validate=True))
    except (ValueError, UnicodeError) as error:
        raise GateError("Invalid private receipt encoding.") from error
    if (receipt.get("contract") != "cohors-primary-private-receipt-1"
            or receipt.get("requestHash") != digest(request)
            or receipt.get("bindingHash") != request["bindingHash"]
            or receipt.get("phase") != request["phase"]):
        raise GateError("Private receipt belongs to a different request or deployment.")
    return receipt


def run_private(engine, operation, request):
    from primary_bootstrap import assert_owned_id
    assert_owned_id(request["vmId"], engine.manifest)
    binding = engine.manifest["releaseBundle"]
    identifier = request["vmId"] + "/runCommands/" + operation
    url = ARM + identifier + "?api-version=" + COMPUTE_VERSION
    script = bootstrap_script(binding)
    safe_body = {"request": request, "scriptSha256": digest(script), "artifactDigest": binding["artifactDigest"]}
    if engine.journal.begin(operation, "PUT", url, safe_body):
        value = {"location": engine.manifest["location"],
                 "tags": {"cohorsBinding": engine.manifest["bindingHash"]},
                 "properties": {"source": {"script": script}, "asyncExecution": True,
                                "timeoutInSeconds": 1800,
                                "parameters": [{"name": "COHORS_REQUEST",
                                                "value": base64.b64encode(canonical(request)).decode()}],
                                "protectedParameters": [{"name": "COHORS_ARTIFACT_URI", "value": artifact_url(binding)}]}}
        engine.client.request("PUT", url, value, allowed=(200, 201, 202))
        until = time.monotonic() + 1800
        while time.monotonic() < until:
            state = engine.client.get(url + "&$expand=instanceView")
            execution = state.get("properties", {}).get("instanceView", {}).get("executionState")
            if execution == "Succeeded":
                receipt = receipt_from_command(state, request)
                engine.journal.complete(operation, {"id": identifier, "receipt": receipt})
                return receipt["result"]
            if execution in ("Failed", "Canceled", "TimedOut"):
                raise GateError("Private execution failed; journal intent retained, no automatic rerun.")
            if execution not in (None, "Pending", "Running", "Unknown"):
                raise GateError("Unrecognized Run Command execution state.")
            time.sleep(10)
        raise GateError("Private execution timeout; reconcile the same command, never replace/recreate it blindly.")
    return engine.outputs[operation]["receipt"]["result"]


def verify_model(engine):
    manifest = engine.manifest
    model = manifest["model"]
    identifier = engine.outputs["phase-foundation"]["platform"]["resources"]["foundry"]
    deployment = engine.client.get(ARM + identifier + "/deployments/" + model["deploymentName"] + "?api-version=2025-06-01")
    account = engine.client.get(ARM + identifier + "?api-version=2025-06-01")
    properties = deployment["properties"]
    if (properties.get("provisioningState") != "Succeeded" or properties["model"].get("name") != model["name"]
            or properties["model"].get("version") != model["version"]
            or properties.get("versionUpgradeOption") != "NoAutoUpgrade"
            or properties["model"].get("format") != "OpenAI"
            or deployment["sku"]["name"] != "Standard" or account["location"].lower() != model["region"]
            or account["properties"].get("publicNetworkAccess") != "Disabled"
            or account["properties"].get("disableLocalAuth") is not True):
        raise GateError("Actual deployed model/version/SKU/region/upgrade configuration differs from the review.")
    return {"deploymentId": deployment["id"], "observedAtUtc": timestamp(),
            "model": properties["model"], "sku": deployment["sku"], "region": account["location"],
            "noAutoUpgrade": True}


def inventory(engine, *, allow_deleted=False):
    from primary_bootstrap import own_group_ids
    result = {}
    for index, group in enumerate(own_group_ids(engine.manifest)):
        if allow_deleted and "cleanup-rg-" + str(index) in engine.outputs:
            continue
        rows = engine.client.collection(ARM + group + "/resources?api-version=2021-04-01")
        owned = {row["id"].lower() for row in rows
                 if row.get("tags", {}).get("cohorsBinding") == engine.manifest["bindingHash"]}
        for row in rows:
            if row["id"].lower() in owned:
                continue
            # Managed OS disks have no inherited tag; their exact VM link must be read back.
            if row.get("type", "").lower() == "microsoft.compute/disks":
                disk = engine.client.get(ARM + row["id"] + "?api-version=2024-03-02")
                if disk.get("managedBy", "").lower() in owned:
                    owned.add(row["id"].lower())
                    continue
            raise GateError("An unowned/untagged resource is present in an own group; stop rather than adopt/delete it.")
        result[group] = sorted(owned)
    return result


def stop(engine):
    stage = engine.outputs.get("phase-runtime")
    if not stage:
        return {"runtimeNotCreated": True, "costsStopped": False}
    for role, identifier in stage["platform"]["runtimeResources"].items():
        if role in ("api", "agents"):
            revisions = engine.client.collection(ARM + identifier + "/revisions?api-version=2025-07-01")
            for revision in revisions:
                if revision["properties"].get("active"):
                    engine.effect("stop-" + role + "-" + revision["name"].split("/")[-1], "POST",
                                  ARM + revision["id"] + "/deactivate?api-version=2025-07-01", allowed=(200, 202, 204))
        else:
            engine.effect("stop-" + role, "POST", ARM + identifier + "/stop?api-version=2024-11-01",
                          allowed=(200, 202, 204))
    client_vm = stage["access"].get("clientVmId")
    if client_vm:
        engine.effect("stop-client", "POST", ARM + client_vm + "/deallocate?api-version=" + COMPUTE_VERSION,
                      allowed=(200, 202, 204))
    return {"runtimeStopRequested": True, "operatorRetainedForDeadline": True,
            "dataNetworkBastionAndRetainedDisksStillBillable": True}


def cleanup(engine):
    from primary_bootstrap import own_group_ids, ownership_tags
    captured = latest_inventory(engine)
    if not captured:
        raise GateError("Cleanup needs the actual captured ownership inventory, not generated names alone.")
    current = inventory(engine, allow_deleted=True)
    expected = {group: values for index, (group, values) in enumerate(captured.items())
                if "cleanup-rg-" + str(index) not in engine.outputs}
    if current != expected:
        raise GateError("Resource inventory drifted; explicit ownership review is required before group deletion.")
    for index, group in enumerate(own_group_ids(engine.manifest)):
        engine.effect("cleanup-rg-" + str(index), "DELETE", ARM + group + "?api-version=2024-03-01",
                      allowed=(200, 202, 204), projection=lambda _: {"deletedResourceGroup": group})
    directory = sorted(engine.outputs.items(), key=lambda item: (
        0 if item[0].startswith("sp-") else 1 if item[0].startswith("group-") else 2, item[0]))
    for key, value in directory:
        if not key.startswith(("sp-", "app-", "group-")) or not isinstance(value, dict) or "collection" not in value:
            continue
        collection, identifier = value["collection"], value["objectId"]
        if "cleanup-directory-" + identifier in engine.outputs:
            continue
        url = GRAPH + "/v1.0/" + collection + "/" + identifier
        selected = "id,description" if collection == "groups" else "id,tags"
        actual = engine.client.get(url + "?$select=" + selected)
        marker = ownership_tags(engine.manifest, value["role"])
        if ((collection == "groups" and actual.get("description") != "|".join(marker))
                or (collection != "groups" and not set(marker).issubset(actual.get("tags", [])))):
            raise GateError("Directory cleanup ownership marker changed.")
        engine.effect("cleanup-directory-" + identifier, "DELETE", url, allowed=(204,),
                      projection=lambda _: {"deletedObjectId": identifier})
    role = engine.outputs.get("deadline-role")
    if role:
        engine.effect("cleanup-deadline-role", "DELETE", ARM + role["id"] + "?api-version=2022-04-01",
                      allowed=(200, 204), projection=lambda _: {"deletedRoleDefinitionId": role["id"]})
    return {"resourceGroupsAndOwnDirectoryObjectsDeleted": True, "vaultPurged": False,
            "softDeleteRetentionAndAnyResidualBillingRequireReadback": True}


def latest_inventory(engine):
    events = reversed(engine.journal.state["events"])
    return next((event["details"]["output"] for event in events
                 if event["kind"] == "completed" and event["operation"].startswith("inventory-")), None)


def checkpoint_inventory(engine, phase):
    owned = inventory(engine)
    operation = "inventory-" + phase
    if engine.journal.begin(operation, "READ-OWNED", phase, {"bindingHash": engine.manifest["bindingHash"]}):
        engine.journal.complete(operation, owned)
    elif engine.outputs[operation] != owned:
        raise GateError("Owned inventory changed since this phase's checkpoint.")


def execute_phase(engine, phase, approval):
    if phase == "stop":
        return stop(engine)
    if phase == "cleanup":
        return cleanup(engine)
    if phase == "filter":
        return run_private(engine, "filter-install", host_request(engine, "filter",
                           vm=engine.outputs["phase-network"]["access"]["filterVmId"]))
    if phase == "initialize":
        observation = verify_model(engine)
        run_private(engine, "filter-private-data", host_request(engine, "filter",
                    vm=engine.outputs["phase-network"]["access"]["filterVmId"]))
        result = run_private(engine, "private-initialize", host_request(engine, phase))
        return {**result, "modelObservation": observation}
    if phase == "publish-functions":
        for role in ("workflows", "operations"):
            engine.arm_role("publish-" + role, engine.outputs["phase-runtime"]["platform"]["runtimeResources"][role],
                            engine.outputs["phase-network"]["access"]["identities"]["publisher"]["objectId"],
                            "de139f84-1756-47ae-9be6-808fbbe84772")
    if phase in ("seed", "publish-functions"):
        return run_private(engine, "private-" + phase, host_request(engine, phase))
    raise GateError("Phase has no implemented executor.")


def reconcile(engine, plan):
    unresolved = [event for event in engine.journal.state["events"] if event["kind"] == "intent"
                  and engine.journal.last(event["operation"]) == event]
    observations = []
    for event in unresolved:
        operation, details = event["operation"], event["details"]
        target = details["target"]
        if details["method"] == "PUT" and "/runCommands/" in target:
            request_phase = ("filter" if operation.startswith("filter-") else operation.removeprefix("private-"))
            vm = target.removeprefix(ARM).split("/runCommands/")[0]
            request = host_request(engine, request_phase, vm=vm)
            value = engine.client.get(target + "&$expand=instanceView")
            receipt = receipt_from_command(value, request)
            engine.journal.complete(operation, {"id": value["id"], "receipt": receipt})
            observations.append({"operation": operation, "status": "CompletedFromExactPrivateReceipt"})
        elif details["method"] == "PUT" and operation.startswith("arm-"):
            value = engine.client.get(target)
            params = value["properties"].get("parameters", {})
            if (value["properties"].get("provisioningState") != "Succeeded"
                    or params.get("profile", {}).get("value", {}).get("bindingHash") != engine.manifest["bindingHash"]):
                raise GateError("ARM deployment has no successful matching binding; no automatic replay.")
            engine.journal.complete(operation, {"id": value["id"]})
            observations.append({"operation": operation, "status": "CompletedFromBoundArmDeployment"})
        else:
            # An ambiguous directory POST is not safely adopted by a display-name search.
            observations.append({"operation": operation, "status": "NeedsObjectSpecificReconciliation",
                                 "method": details["method"], "target": target})
    return {"contract": "cohors-primary-reconciliation-1", "bindingHash": engine.manifest["bindingHash"],
            "observations": observations, "cloudMutated": False}
