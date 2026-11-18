"""Staged primary bootstrap. Planning is not deployment authorization."""

import argparse
import base64
import datetime as dt
import ipaddress
import json
import os
from pathlib import Path
import re
import secrets
import subprocess
import sys
import time
import urllib.parse
import uuid

from primary_control import (ARM, GRAPH, Azure, GateError, Journal, bound_client, canonical, digest,
                             control_claims, file_hash, parse_time, read_json, require_guid, timestamp, utcnow, write_json)


ROOT = Path(__file__).resolve().parents[2]
HUMAN_ROLES = ("PmsSpecialist", "MedicalSafetyReviewer", "RegulatoryAffairs", "QualityManager", "Auditor")
PHASES = ("identities", "network", "filter", "foundation", "grants", "initialize",
          "runtime", "seed", "publish-functions", "activate")
EFFECTS = {
    "identities": ["CreateOwnedAppsServicePrincipalsAndGroups", "GrantOwnSpaDelegatedApiScope",
                   "AssignSelectedHumansToOwnBusinessRoles"],
    "network": ["CreateOnlyOwnResourceGroupsNetworkAndAccessVMs", "CreateNvaPublicEgressIp",
                "ProvisionReviewedOptionalBastionAndLicensedClient"],
    "filter": ["InstallPinnedTransparentProxyAndDnsPolicy", "RunCodeOnOwnNva", "KeepUnmatchedTrafficDenied"],
    "foundation": ["CreateOwnPrivateDataAndModelServices", "CreateOwnMonitorAndActionGroup"],
    "grants": ["GrantOwnRuntimeAndTemporaryBootstrapRights", "CreateOwnScopedDeadlineRole"],
    "initialize": ["BindPrivateDataHostsToNoPublicFallbackPolicy", "ImportBoundContainerImages",
                   "ApplyApplicationMigrationsAndSearchSchema", "InstallApprovedDeadlineExecutor"],
    "runtime": ["DeployBoundPrivateApiAgentHostAndFlexHosts"],
    "seed": ["SeedOnlySyntheticBaselineUsingBoundApiStartup"],
    "publish-functions": ["PublishBothBoundFlexPackagesUsingOneDeploy"],
    "activate": ["BindOwnSpaRedirect", "RemoveTemporaryBootstrapDataPrivileges", "EnableReviewedWatchdogAlert"],
    "stop": ["StopOwnRuntimeAndDeallocateOwnOperatorAndClient"],
    "cleanup": ["DeleteOnlyJournalOwnedResourceGroupsAndDirectoryObjects", "RetainVaultSoftDeleteNoPurge"],
}
ROLE_IDS = {
    "reader": "acdd72a7-3385-48ef-bd42-f606fba81ae7",
    "acrPush": "8311e382-0749-4cb8-b61a-304f252e45ec",
    "blobContributor": "ba92f5b4-2d11-453d-a403-e96b0029c9fe",
    "searchServiceContributor": "7ca78c08-252a-4471-8644-bb5ff32d4ba0",
    "websiteContributor": "de139f84-1756-47ae-9be6-808fbbe84772",
}


def stable_id(manifest, name):
    return str(uuid.uuid5(uuid.UUID(manifest["deploymentId"]), name))


def load_manifest(path):
    manifest = read_json(path)
    if manifest.get("contract") != "cohors-primary-bootstrap-1":
        raise GateError("Unsupported primary bootstrap contract.")
    if manifest.get("deploymentAuthorized") is not False:
        raise GateError("Do not turn the architecture flag into deployment authorization; use a bound phase approval.")
    unsigned = {key: value for key, value in manifest.items() if key != "bindingHash"}
    if digest(unsigned) != manifest.get("bindingHash"):
        raise GateError("Start manifest bindingHash mismatch.")
    release = manifest.get("releaseBundle")
    if not isinstance(release, dict):
        raise GateError("The actual immutable hosted package binding is required.")
    patterns = {"applicationCommit": r"[0-9a-f]{40}", "infrastructureCommit": r"[0-9a-f]{40}",
                "artifactDigest": r"sha256:[0-9a-f]{64}", "bundleSha256": r"[0-9a-f]{64}",
                "repository": r"[A-Za-z0-9][A-Za-z0-9_.-]*/[A-Za-z0-9][A-Za-z0-9_.-]*"}
    for key, pattern in patterns.items():
        if not isinstance(release.get(key), str) or not re.fullmatch(pattern, release[key]):
            raise GateError(f"Invalid hosted package {key}; placeholders and script syntax are prohibited.")
    if any(type(release.get(key)) is not int or release[key] <= 0 for key in ("runId", "artifactId")):
        raise GateError("Hosted run and artifact IDs must come from actual completed outputs.")
    require_guid(manifest["deploymentId"], "deploymentId")
    for key in ("tenantId", "subscriptionId", "principalObjectId", "oauthClientId"):
        require_guid(manifest["binding"][key], key)
    for resource in (ARM + "/", GRAPH + "/"):
        control_claims(manifest["binding"], manifest.get("controlTokenBindings"), resource)
    if manifest["location"] != "francecentral":
        raise GateError("The reviewed primary profile is France Central, not an automatic regional fallback.")
    if not re.fullmatch(r"[a-z][a-z0-9]{2,9}", manifest["namePrefix"]):
        raise GateError("Invalid generated primary prefix.")
    created, expires = parse_time(manifest["createdAtUtc"]), parse_time(manifest["expiresAtUtc"])
    if not dt.timedelta(0) < expires - created <= dt.timedelta(days=14):
        raise GateError("The pilot lifetime must be positive and at most fourteen days; resume never resets it.")
    for suffix in ("workload", "access"):
        if manifest["names"][suffix + "ResourceGroup"] != f"rg-{manifest['namePrefix']}-{suffix}":
            raise GateError("Resource group names must be generated within this primary deployment.")
    network = manifest["network"]
    vnet = ipaddress.ip_network(network["vnet"], strict=True)
    children = [ipaddress.ip_network(network[key], strict=True) for key in
                ("containerApps", "workflows", "operations", "privateEndpoints", "egress", "operator", "client", "bastion")]
    if vnet.version != 4 or any(not child.subnet_of(vnet) for child in children):
        raise GateError("All own subnets must be IPv4 ranges inside the selected VNet.")
    if any(first.overlaps(second) for i, first in enumerate(children) for second in children[i + 1:]):
        raise GateError("Primary subnet allocations overlap.")
    if network["nvaPrivateIp"] != str(ipaddress.ip_network(network["egress"])[4]):
        raise GateError("The NVA address must match its deterministic own-subnet allocation.")
    if manifest.get("azureFirewall") is not False or manifest.get("vpn") is not False:
        raise GateError("This profile does not select Azure Firewall or VPN.")
    if manifest.get("containerAppsProfile") not in ("Consumption", "D4"):
        raise GateError("The actual ACA hosting profile must be selected explicitly, with its costs and DNS boundary.")
    if manifest["model"]["sku"] != "Standard" or manifest["model"]["autoUpgrade"] is not False:
        raise GateError("No Global/DataZone/developer model or automatic model upgrade is allowed.")
    if manifest["model"]["region"] not in ("francecentral", "swedencentral", "westeurope",
                                           "northeurope", "germanywestcentral"):
        raise GateError("The model needs an explicitly reviewed EU-member-state region, never a fallback.")
    if (manifest["model"]["enabled"] and
            (not re.fullmatch(r"[a-z0-9][a-z0-9.-]{0,80}", manifest["model"]["name"])
             or not re.fullmatch(r"[A-Za-z0-9.-]{1,80}", manifest["model"]["version"])
             or manifest["model"]["version"].lower() in ("latest", "default")
             or type(manifest["model"]["capacity"]) is not int or manifest["model"]["capacity"] < 1)):
        raise GateError("An evaluation deployment needs a real fixed name/version and reviewed positive capacity.")
    for image_key in ("linuxImage", "windowsImage"):
        image = manifest["access"][image_key]
        if image and (not image.get("version") or image["version"].lower() == "latest"):
            raise GateError("Access images require an actual immutable catalog version.")
    actors = manifest.get("actors", [])
    for actor in actors:
        require_guid(actor["objectId"], "resolved actor objectId")
        if not actor["roles"] or any(role not in HUMAN_ROLES for role in actor["roles"]):
            raise GateError("A business actor has an unsupported role.")
        if {"MedicalSafetyReviewer", "RegulatoryAffairs"}.issubset(actor["roles"]):
            raise GateError("Medical and regulatory approval must remain different people.")
        if not actor["products"] or not set(actor["products"]).issubset(manifest["products"]):
            raise GateError("Business product authorization must be explicit.")
    actor_ids = [actor["objectId"] for actor in actors]
    if len(actor_ids) != len(set(actor_ids)):
        raise GateError("Resolve each actor only once; do not bypass separation with duplicate rows.")
    expected_grants = {actor["objectId"]: actor["products"] for actor in actors}
    if manifest["productGrants"] != expected_grants:
        raise GateError("Product grants must exactly match the resolved actor bindings.")
    return manifest


def source_binding():
    files = sorted(path for directory in ("infra",) for path in (ROOT / directory).rglob("*")
                   if path.is_file() and path.suffix in (".bicep", ".py", ".sh", ".ps1", ".txt", ".json", ".yml", ".cs", ".csproj", ".kql")
                   and not {"__pycache__", "obj", "bin", "fabric"}.intersection(path.parts))
    files += [(ROOT / ".github" / "workflows" / name) for name in
              ("cohors-ci.yml", "cohors-deploy.yml", "cohors-primary-package.yml")]
    return {str(path.relative_to(ROOT)).replace("\\", "/"): file_hash(path) for path in files}


def require_committed_controller(manifest):
    head = subprocess.run(["git", "-C", str(ROOT), "rev-parse", "HEAD"], capture_output=True,
                          timeout=30, check=False)
    if head.returncode or head.stdout.decode().strip() != manifest["releaseBundle"]["infrastructureCommit"]:
        raise GateError("Controller source must be the same immutable infrastructure commit as the hosted package.")
    changed = subprocess.run(["git", "-C", str(ROOT), "status", "--porcelain", "--untracked-files=all",
                              "--", "infra", ".github/workflows"], capture_output=True, timeout=30, check=False)
    if changed.returncode:
        raise GateError("Could not verify the controller's actual source tree.")
    lines = [line for line in changed.stdout.decode().splitlines()
             if not any(part in line.replace("\\", "/").split("/") for part in ("__pycache__", "obj", "bin"))]
    if lines:
        raise GateError("Infrastructure/controller files changed after the bound hosted build.")


def own_group_ids(manifest):
    subscription = manifest["binding"]["subscriptionId"]
    return [f"/subscriptions/{subscription}/resourceGroups/{manifest['names'][kind + 'ResourceGroup']}"
            for kind in ("workload", "access")]


def assert_owned_id(identifier, manifest):
    if not any(identifier.lower() == group.lower() or identifier.lower().startswith(group.lower() + "/")
               for group in own_group_ids(manifest)):
        raise GateError("Resource is outside this deployment's generated resource groups.")


def approval_check(approval, plan, manifest, now=None):
    now = now or utcnow()
    if approval.get("contract") != "cohors-primary-phase-approval-1":
        raise GateError("A concrete, separate phase approval is required.")
    if approval.get("bindingHash") != manifest["bindingHash"] or approval.get("planHash") != digest(plan):
        raise GateError("Approval does not bind this exact manifest and phase plan.")
    if approval.get("phase") != plan["phase"] or approval.get("acknowledgedEffects") != EFFECTS[plan["phase"]]:
        raise GateError("Approval does not acknowledge the exact privileged effects.")
    if approval.get("reviewerObjectId") != manifest["binding"]["principalObjectId"]:
        raise GateError("Approval must identify the selected initial operator, not a substituted principal.")
    expires, reviewed = parse_time(approval["expiresAtUtc"]), parse_time(approval["reviewedAtUtc"])
    if not reviewed <= now < expires or expires - reviewed > dt.timedelta(hours=24):
        raise GateError("Phase approval is expired, future-dated, or exceeds twenty-four hours.")
    if now >= parse_time(manifest["expiresAtUtc"]) and plan["phase"] not in ("stop", "cleanup"):
        raise GateError("Pilot deadline elapsed; only targeted stop/cleanup remains.")
    if approval.get("riskAcknowledged") is not True:
        raise GateError("The phase's concrete risk acknowledgement is missing.")
    for key in ("policyReviewSha256", "readinessEvidenceSha256", "costReviewSha256"):
        if not re.fullmatch(r"[0-9a-f]{64}", approval.get(key, "")):
            raise GateError(f"A real bound {key} is required.")
    if plan["phase"] in ("network", "foundation", "runtime", "activate"):
        if not re.fullmatch(r"[0-9a-f]{64}", approval.get("whatIfSha256", "")):
            raise GateError("The additive what-if result must be separately reviewed and bound.")
    if plan["phase"] in ("network", "foundation") and manifest.get("containerAppsProfile") != "D4":
        raise GateError("Consumption requires AzurePlatformDNS and cannot use this strict-DNS candidate. Dedicated D4 is an unselected costed alternative, not an automatic fallback.")
    if plan["phase"] in ("runtime", "seed", "publish-functions", "activate"):
        contract = manifest.get("operationsSqlContract")
        if not contract or contract.get("applicationCommit") != manifest["releaseBundle"]["applicationCommit"]:
            raise GateError("A built, source-bound restricted Operations SQL contract is required. The b7 generic JSON writer is not eligible and cannot be accepted by a risk waiver.")
        raise GateError("No restricted Operations grant adapter is bound to this installer revision yet. A metadata claim or failed/unbuilt primary package cannot activate the host.")


def app_body(manifest, role):
    scopes = []
    roles = []
    if role == "api":
        scopes = [{"id": stable_id(manifest, "scope:access_as_user"), "value": "access_as_user",
                   "type": "Admin", "isEnabled": True,
                   "adminConsentDisplayName": "Use the synthetic Cohors pilot",
                   "adminConsentDescription": "Delegated access to this isolated synthetic pilot only."}]
        roles = [{"id": stable_id(manifest, "role:" + value), "value": value, "displayName": value,
                  "description": "Explicit synthetic Cohors role; no external medical or regulatory authorization.",
                  "isEnabled": True, "allowedMemberTypes": ["User"]} for value in HUMAN_ROLES]
        roles.append({"id": stable_id(manifest, "role:Workflow"), "value": "Workflow",
                      "displayName": "Workflow", "description": "Own Durable workload caller only.",
                      "isEnabled": True, "allowedMemberTypes": ["Application"]})
    if role == "agents":
        roles = [{"id": stable_id(manifest, "role:Agent"), "value": "Agent", "displayName": "Agent",
                  "description": "Own API managed-identity caller only.", "isEnabled": True,
                  "allowedMemberTypes": ["Application"]}]
    body = {"displayName": f"{manifest['namePrefix']}-{role}", "signInAudience": "AzureADMyOrg",
            "tags": ownership_tags(manifest, role),
            "api": {"requestedAccessTokenVersion": 2, "oauth2PermissionScopes": scopes},
            "appRoles": roles,
            "optionalClaims": {"accessToken": [{"name": "idtyp", "essential": False}]}}
    if role == "spa":
        body["spa"] = {"redirectUris": []}
    return body


def ownership_tags(manifest, role):
    return [f"CohorsPrimary:{manifest['deploymentId']}", f"CohorsBinding:{manifest['bindingHash']}",
            f"CohorsRole:{role}"]


class Bootstrap:
    def __init__(self, manifest, journal, client):
        self.manifest = manifest
        self.journal = journal
        self.client = client
        self.outputs = journal.state["outputs"]

    def effect(self, operation, method, url, body=None, *, allowed=(200, 201, 204), projection=None):
        if not self.journal.begin(operation, method, url, body):
            return self.outputs[operation]
        result, headers, status = self.client.request(method, url, body, allowed=allowed)
        if status == 202:
            location = headers.get("Azure-AsyncOperation") or headers.get("azure-asyncoperation") or headers.get("Location")
            if not location:
                raise GateError("Accepted operation has no safe polling reference; explicit reconciliation required.")
            self.client.poll(location)
        output = projection(result) if projection else result
        self.journal.complete(operation, output)
        return output

    def owned_graph(self, collection, operation, body, role):
        endpoint = GRAPH + "/v1.0/" + collection
        is_group = collection == "groups"
        marker = "|".join(ownership_tags(self.manifest, role))
        if is_group:
            body = {**body, "description": marker}
        selected = "id,displayName,description" if is_group else "id,appId,displayName,tags"
        previous = self.outputs.get(operation)
        if previous:
            current = self.client.get(endpoint + "/" + previous["objectId"] + "?$select=" + selected)
            owned = (current.get("description") == marker if is_group
                     else set(ownership_tags(self.manifest, role)).issubset(current.get("tags", [])))
            if not owned:
                raise GateError("Previously created directory object's ownership marker changed.")
        else:
            lookup = endpoint + "?" + urllib.parse.urlencode({
                "$filter": "displayName eq '" + body["displayName"] + "'", "$select": selected})
            if self.client.collection(lookup):
                raise GateError("A matching directory display name already exists; no implicit adoption.")
        return self.effect(operation, "POST", endpoint, body, allowed=(201,),
                           projection=lambda value: {"objectId": value["id"], "clientId": value.get("appId"),
                                                     "displayName": value["displayName"], "role": role,
                                                     "collection": collection})

    def identities(self):
        identities = {}
        for role in ("api", "agents", "spa"):
            application = self.owned_graph("applications", "app-" + role, app_body(self.manifest, role), role)
            sp_body = {"appId": application["clientId"], "displayName": application["displayName"],
                       "appRoleAssignmentRequired": True, "tags": ownership_tags(self.manifest, role)}
            principal = self.owned_graph("servicePrincipals", "sp-" + role, sp_body, role)
            identities[role] = {**application, "servicePrincipalObjectId": principal["objectId"]}
            if role != "spa":
                self.effect("identifier-" + role, "PATCH",
                            GRAPH + "/v1.0/applications/" + application["objectId"],
                            {"identifierUris": ["api://" + application["clientId"]]})
        for role in ("sql-admin", *HUMAN_ROLES):
            group = self.owned_graph("groups", "group-" + role, {
                "displayName": self.manifest["namePrefix"] + "-" + role, "mailEnabled": False,
                "mailNickname": self.manifest["namePrefix"] + re.sub(r"[^a-z0-9]", "", role.lower()),
                "securityEnabled": True, "groupTypes": [],
                "description": "Isolated synthetic Cohors bootstrap group. No directory role.",
                # Graph groups have no writable tags property. Use a bound description marker.
            }, role)
            identities["sqlAdmin" if role == "sql-admin" else role] = group
        for role in HUMAN_ROLES:
            group = identities[role]
            self.effect("business-role-" + role, "POST",
                        GRAPH + f"/v1.0/groups/{group['objectId']}/appRoleAssignments",
                        {"principalId": group["objectId"], "resourceId": identities["api"]["servicePrincipalObjectId"],
                         "appRoleId": stable_id(self.manifest, "role:" + role)}, allowed=(201,))
            self.effect("spa-assignment-" + role, "POST",
                        GRAPH + f"/v1.0/groups/{group['objectId']}/appRoleAssignments",
                        {"principalId": group["objectId"], "resourceId": identities["spa"]["servicePrincipalObjectId"],
                         "appRoleId": "00000000-0000-0000-0000-000000000000"}, allowed=(201,))
        for actor in self.manifest["actors"]:
            user = self.client.get(GRAPH + f"/v1.0/users/{actor['objectId']}?$select=id,accountEnabled,userType")
            if user.get("accountEnabled") is not True:
                raise GateError("A selected business actor is disabled or not verifiable.")
            for role in actor["roles"]:
                self.effect("member-" + role + "-" + actor["objectId"], "POST",
                            GRAPH + f"/v1.0/groups/{identities[role]['objectId']}/members/$ref",
                            {"@odata.id": GRAPH + "/v1.0/directoryObjects/" + actor["objectId"]}, allowed=(204,))
        self.effect("spa-required-access", "PATCH",
                    GRAPH + "/v1.0/applications/" + identities["spa"]["objectId"],
                    {"requiredResourceAccess": [{"resourceAppId": identities["api"]["clientId"],
                                                 "resourceAccess": [{"id": stable_id(self.manifest, "scope:access_as_user"),
                                                                     "type": "Scope"}]}]})
        self.effect("spa-delegated-grant", "POST", GRAPH + "/v1.0/oauth2PermissionGrants",
                    {"clientId": identities["spa"]["servicePrincipalObjectId"], "consentType": "AllPrincipals",
                     "resourceId": identities["api"]["servicePrincipalObjectId"], "scope": "access_as_user"},
                    allowed=(201,), projection=lambda value: {"id": value["id"]})
        return identities

    def roles(self, phase_outputs):
        platform, access = phase_outputs["platform"], phase_outputs["access"]
        private_identities = access["identities"]
        runtime = {value["role"]: value for value in platform["identities"]}
        directory = self.outputs["phase-identities"]
        self.effect("sql-bootstrap-member", "POST",
                    GRAPH + f"/v1.0/groups/{directory['sqlAdmin']['objectId']}/members/$ref",
                    {"@odata.id": GRAPH + "/v1.0/directoryObjects/" + private_identities["bootstrap"]["objectId"]},
                    allowed=(204,))
        for name, role_name, scope, identity in (
            ("publisher-acr", "acrPush", platform["resources"]["registry"], "publisher"),
            ("bootstrap-evidence", "blobContributor", platform["resources"]["evidence"], "bootstrap"),
            ("bootstrap-search", "searchServiceContributor", platform["resources"]["search"], "bootstrap"),
        ):
            self.arm_role(name, scope, private_identities[identity]["objectId"], ROLE_IDS[role_name])
        for caller, resource, role in (("api", "agents", "Agent"), ("workflows", "api", "Workflow")):
            self.effect("runtime-app-role-" + caller, "POST",
                        GRAPH + f"/v1.0/servicePrincipals/{runtime[caller]['objectId']}/appRoleAssignments",
                        {"principalId": runtime[caller]["objectId"],
                         "resourceId": directory[resource]["servicePrincipalObjectId"],
                         "appRoleId": stable_id(self.manifest, "role:" + role)}, allowed=(201,))
        for principal in self.manifest["access"]["clientOperatorObjectIds"]:
            for index, scope in enumerate(access["clientReaderScopes"]):
                self.arm_role(f"client-reader-{principal}-{index}", scope, principal, ROLE_IDS["reader"], "User")
        role_id = stable_id(self.manifest, "deadline-role")
        definition_path = (f"/subscriptions/{self.manifest['binding']['subscriptionId']}"
                           f"/providers/Microsoft.Authorization/roleDefinitions/{role_id}")
        self.effect("deadline-role", "PUT", ARM + definition_path + "?api-version=2022-04-01", {
            "properties": {"roleName": self.manifest["namePrefix"] + "-bounded-deadline", "type": "CustomRole",
                           "description": "Delete only the two own ephemeral pilot resource groups at the approved deadline.",
                           "assignableScopes": own_group_ids(self.manifest),
                           "permissions": [{"actions": ["Microsoft.Resources/subscriptions/resourceGroups/read",
                                                         "Microsoft.Resources/subscriptions/resourceGroups/delete",
                                                         "Microsoft.Resources/subscriptions/resourceGroups/resources/read"],
                                            "notActions": [], "dataActions": [], "notDataActions": []}]}},
                    projection=lambda value: {"id": value["id"]})
        for index, scope in enumerate(own_group_ids(self.manifest)):
            self.arm_role(f"deadline-assignment-{index}", scope, private_identities["deadline"]["objectId"], role_id)
        return {"temporaryBootstrapPrivileges": True, "runtimeIdentities": runtime,
                "deadlineRoleDefinitionId": definition_path}

    def arm_role(self, name, scope, principal, role_id, principal_type="ServicePrincipal"):
        assert_owned_id(scope, self.manifest)
        identifier = scope + "/providers/Microsoft.Authorization/roleAssignments/" + stable_id(self.manifest, name)
        return self.effect(name, "PUT", ARM + identifier + "?api-version=2022-04-01",
                           {"properties": {"principalId": principal, "principalType": principal_type,
                            "roleDefinitionId": f"/subscriptions/{self.manifest['binding']['subscriptionId']}"
                                                "/providers/Microsoft.Authorization/roleDefinitions/" + role_id}},
                           projection=lambda value: {"id": value["id"]})


def compile_bicep(client, filename):
    value = client.credential.cli(["bicep", "build", "--file", str(ROOT / "infra" / filename), "--stdout"])
    if not isinstance(value, dict) or "$schema" not in value:
        raise GateError("Bicep did not produce an ARM template.")
    version = value.get("metadata", {}).get("_generator", {}).get("version", "")
    if not re.match(r"^0\.49\.2(?:\.|$)", version):
        raise GateError("The controller compiler differs from the locally/hosted validated Bicep version.")
    return value


def phase_plan(manifest, phase, outputs):
    if phase in PHASES and phase != PHASES[0]:
        previous = PHASES[PHASES.index(phase) - 1]
        if "phase-" + previous not in outputs:
            raise GateError(f"Phase {previous} must complete first; no prebuilt IDs may replace its outputs.")
    plan = {"contract": "cohors-primary-phase-plan-1", "bindingHash": manifest["bindingHash"],
            "phase": phase, "sourceFiles": source_binding(), "effects": EFFECTS[phase],
            "ownedResourceGroups": own_group_ids(manifest), "priorOutputsHash": digest(outputs),
            "priorOutputHashes": {key: digest(value) for key, value in outputs.items()},
            "cloudMutationAuthorized": False, "createdAtUtc": timestamp()}
    if phase == "identities":
        plan["directoryChanges"] = {
            "applications": [app_body(manifest, role) for role in ("api", "agents", "spa")],
            "securityGroups": [manifest["namePrefix"] + "-" + role for role in ("sql-admin", *HUMAN_ROLES)],
            "actors": manifest["actors"], "delegatedConsent": {"scope": "access_as_user",
            "client": "new-own-spa", "resource": "new-own-api", "consentType": "AllPrincipals",
            "applicationAssignmentRequired": True},
            "noExistingAppCredentialsOrTenantSettingsChanged": True}
    if phase in ("network", "foundation", "runtime", "activate"):
        plan["template"] = "primary-network.bicep" if phase == "network" else "primary.bicep"
    return plan


def require_plan(plan, manifest, outputs):
    if plan.get("contract") != "cohors-primary-phase-plan-1" or plan.get("bindingHash") != manifest["bindingHash"]:
        raise GateError("Plan/manifest binding mismatch.")
    if plan["sourceFiles"] != source_binding():
        raise GateError("Source changed after phase planning.")
    if plan["effects"] != EFFECTS[plan["phase"]] or plan["ownedResourceGroups"] != own_group_ids(manifest):
        raise GateError("Plan effects or ownership scope changed.")
    if any(key not in outputs or digest(outputs[key]) != expected
           for key, expected in plan["priorOutputHashes"].items()):
        raise GateError("A prerequisite output changed after phase planning.")
    # Completed effects of this same phase may exist on explicit resume, but no other phase may appear.
    phase = plan["phase"]
    if "phase-" + phase in outputs:
        raise GateError("This phase is already complete; inspect it rather than running it again.")
    if any(key.startswith("phase-") and key not in plan["priorOutputHashes"] for key in outputs):
        raise GateError("Another phase completed after this plan; create and review a new phase plan.")


def arm_parameters(manifest, phase, outputs, *, os_credentials=None):
    params = {"profile": {"value": manifest}}
    if phase == "network":
        params["sshPublicKey"] = {"value": manifest["access"]["sshPublicKey"]}
        params["windowsBootstrapPassword"] = {"value": (os_credentials or {}).get("windowsPassword", "")}
    else:
        params["identities"] = {"value": outputs["phase-identities"]}
        params["networkPhase"] = {"value": outputs["phase-network"]}
        params["deployRuntime"] = {"value": phase in ("runtime", "activate")}
        params["enableWatchdogAlert"] = {"value": phase == "activate"}
        params["release"] = {"value": outputs.get("phase-initialize", {}).get("release", {})}
    return params


def validate_what_if(value, manifest):
    if value.get("status") != "Succeeded":
        raise GateError("What-if did not complete successfully.")
    if value.get("error"):
        raise GateError("What-if reported a provider error.")
    changes = value.get("changes")
    if not isinstance(changes, list):
        raise GateError("What-if changes are missing.")
    for change in changes:
        identifier = change.get("resourceId", "")
        assert_owned_id(identifier, manifest)
        if change.get("changeType") not in ("Create", "Modify", "NoChange", "Ignore"):
            raise GateError("What-if contains Delete, Deploy, Unsupported or another unreviewable change.")
        if change["changeType"] in ("Modify", "NoChange", "Ignore"):
            before = change.get("before") or {}
            # Child resources inherit ownership from their separately verified parent group.
            if before.get("tags") and before["tags"].get("cohorsBinding") != manifest["bindingHash"]:
                raise GateError("What-if would modify a resource with another ownership binding.")
        if change.get("changeType") == "Ignore":
            raise GateError("Ignored what-if changes are not a complete additive preview.")
    return changes


def check_owned_groups(client, manifest, *, must_exist):
    for identifier in own_group_ids(manifest):
        resource, _, status = client.request("GET", ARM + identifier + "?api-version=2024-03-01",
                                             allowed=(200, 404))
        if status == 404:
            if must_exist:
                raise GateError("An owned resource group is missing; explicit reconciliation required.")
        elif not must_exist or resource.get("tags", {}).get("cohorsBinding") != manifest["bindingHash"]:
            raise GateError("A generated resource group already exists or has a different owner; no adoption.")


def arm_apply(engine, phase, plan, approval, preview_path):
    manifest, client = engine.manifest, engine.client
    preview = read_json(preview_path)
    if file_hash(preview_path) != approval["whatIfSha256"] or preview.get("planHash") != digest(plan):
        raise GateError("Reviewed what-if file does not match the exact phase plan.")
    validate_what_if(preview["result"], manifest)
    previous_arm = engine.outputs.get("arm-" + phase)
    if previous_arm:
        deployment = client.get(ARM + previous_arm["id"] + "?api-version=2025-04-01")
        if deployment["properties"].get("provisioningState") != "Succeeded":
            client.poll(ARM + previous_arm["id"] + "?api-version=2025-04-01")
            deployment = client.get(ARM + previous_arm["id"] + "?api-version=2025-04-01")
        return {name: item["value"] for name, item in deployment["properties"]["outputs"].items()}
    check_owned_groups(client, manifest, must_exist=phase != "network")
    credentials = {"windowsPassword": secrets.token_urlsafe(40) + "aA1!"} if (
        phase == "network" and manifest["access"]["includeInteractiveClient"]) else {}
    template = compile_bicep(client, plan["template"])
    if digest(template) != preview["compiledTemplateHash"]:
        raise GateError("Compiled ARM differs from the preview.")
    parameters = arm_parameters(manifest, phase, engine.outputs, os_credentials=credentials)
    identifier = (f"/subscriptions/{manifest['binding']['subscriptionId']}/providers/Microsoft.Resources/"
                  f"deployments/{manifest['namePrefix']}-{phase}")
    url = ARM + identifier + "?api-version=2025-04-01"
    body = {"location": manifest["location"], "properties": {
        "mode": "Incremental", "template": template, "parameters": parameters}}
    engine.effect("arm-" + phase, "PUT", url, body, allowed=(200, 201, 202),
                  projection=lambda _: {"id": identifier})
    deployment = client.get(url)
    if deployment["properties"].get("provisioningState") != "Succeeded":
        client.poll(url)
        deployment = client.get(url)
    return {name: item["value"] for name, item in deployment["properties"]["outputs"].items()}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("plan", "inspect", "what-if", "apply", "reconcile"))
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--journal", type=Path, required=True)
    parser.add_argument("--phase", choices=(*PHASES, "stop", "cleanup"))
    parser.add_argument("--plan", type=Path)
    parser.add_argument("--approval", type=Path)
    parser.add_argument("--what-if", dest="preview", type=Path)
    parser.add_argument("--out", type=Path)
    args = parser.parse_args(argv)
    manifest = load_manifest(args.manifest)
    with Journal(args.journal, manifest["bindingHash"]) as journal:
        if args.command == "inspect":
            print(json.dumps({"bindingHash": manifest["bindingHash"],
                              "completedPhases": [key[6:] for key in journal.state["outputs"] if key.startswith("phase-")],
                              "pendingOperations": [event["operation"] for event in journal.state["events"]
                                                    if journal.last(event["operation"]) == event and event["kind"] == "intent"]}))
            return 0
        if args.command == "plan":
            if not args.phase or not args.out:
                raise GateError("Planning requires --phase and a new private --out file.")
            plan = phase_plan(manifest, args.phase, journal.state["outputs"])
            write_json(args.out, plan)
            print("Phase plan prepared; no cloud access or deployment authorization.")
            return 0
        if not args.plan:
            raise GateError("--plan is required.")
        require_committed_controller(manifest)
        plan = read_json(args.plan)
        require_plan(plan, manifest, journal.state["outputs"])
        if args.command == "reconcile":
            if not args.out:
                raise GateError("Reconciliation requires a new private report file.")
            from primary_release import reconcile
            with bound_client(manifest["binding"], manifest["controlTokenBindings"]) as client:
                result = reconcile(Bootstrap(manifest, journal, client), plan)
                write_json(args.out, result)
            print("Read-only reconciliation recorded; no cloud retry or mutation.")
            return 0
        if args.command == "what-if":
            if plan["phase"] not in ("network", "foundation", "runtime", "activate") or not args.out:
                raise GateError("Only ARM phases have what-if; directory effects use their concrete phase plan.")
            with bound_client(manifest["binding"], manifest["controlTokenBindings"], allow_writes=True) as client:
                check_owned_groups(client, manifest, must_exist=plan["phase"] != "network")
                template = compile_bicep(client, plan["template"])
                parameters = arm_parameters(manifest, plan["phase"], journal.state["outputs"],
                                            os_credentials={"windowsPassword": secrets.token_urlsafe(40) + "aA1!"})
                path = (f"/subscriptions/{manifest['binding']['subscriptionId']}/providers/Microsoft.Resources/"
                        f"deployments/{manifest['namePrefix']}-{plan['phase']}/whatIf?api-version=2025-04-01")
                result, headers, status = client.request("POST", ARM + path, {
                    "location": manifest["location"], "properties": {"mode": "Incremental", "template": template,
                    "parameters": parameters, "whatIfSettings": {"resultFormat": "FullResourcePayloads"},
                    "validationLevel": "ProviderNoRbac"}}, allowed=(200, 202))
                if status == 202:
                    poll_url = headers.get("Location") or headers.get("location")
                    if not poll_url:
                        raise GateError("What-if polling reference is missing.")
                    result = client.poll(poll_url)
                validate_what_if(result, manifest)
                # Do not persist the secure Windows parameter or full before/after resource properties.
                for change in result.get("changes", []):
                    for side in ("before", "after"):
                        profile = (change.get(side) or {}).get("properties", {}).get("osProfile", {})
                        profile.pop("adminPassword", None)
                write_json(args.out, {"contract": "cohors-primary-what-if-1", "planHash": digest(plan),
                                     "compiledTemplateHash": digest(template), "result": result,
                                     "observedAtUtc": timestamp(), "createAuthorizationProven": False})
            print("Additive what-if observed; no apply.")
            return 0
        if not args.approval:
            raise GateError("Apply requires a separate --approval, never the architecture authorization flag.")
        approval = read_json(args.approval)
        approval_check(approval, plan, manifest)
        from primary_model_gate import require_model_acceptance
        model_acceptance = require_model_acceptance(manifest, plan["phase"], approval, args.approval.parent,
                                                    journal.state["outputs"])
        for key in ("policyReviewSha256", "readinessEvidenceSha256", "costReviewSha256"):
            relative = Path(approval.get("evidenceFiles", {}).get(key, ""))
            if not relative.parts or relative.is_absolute() or ".." in relative.parts:
                raise GateError("Review evidence must be explicitly bound inside the private approval directory.")
            if file_hash(args.approval.parent / relative) != approval[key]:
                raise GateError("A reviewed evidence file is missing or its bytes changed.")
        phase = plan["phase"]
        with bound_client(manifest["binding"], manifest["controlTokenBindings"], allow_writes=True) as client:
            engine = Bootstrap(manifest, journal, client)
            if model_acceptance:
                from primary_release import verify_model
                fresh = verify_model(engine)
                expected = engine.outputs["phase-initialize"]["modelObservation"]
                if ({key: value for key, value in fresh.items() if key != "observedAtUtc"}
                        != {key: value for key, value in expected.items() if key != "observedAtUtc"}):
                    raise GateError("Model configuration changed since its bound live acceptance; no automatic upgrade/fallback.")
            if phase == "identities":
                result = engine.identities()
            elif phase in ("network", "foundation", "runtime", "activate"):
                if not args.preview:
                    raise GateError("ARM apply requires the exact reviewed --what-if file.")
                result = arm_apply(engine, phase, plan, approval, args.preview)
            elif phase == "grants":
                result = engine.roles(engine.outputs["phase-foundation"])
            else:
                from primary_release import execute_phase
                result = execute_phase(engine, phase, approval)
            if phase in ("network", "foundation", "grants", "initialize", "runtime", "seed", "publish-functions", "activate"):
                from primary_release import checkpoint_inventory
                checkpoint_inventory(engine, phase)
            if model_acceptance:
                result["modelAcceptance"] = model_acceptance
            operation = "phase-" + phase
            journal.begin(operation, "LOCAL-CHECKPOINT", phase, {"planHash": digest(plan)})
            journal.complete(operation, result)
        print(f"Phase {phase} completed and checkpointed. Live acceptance is a separate result.")
        return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except GateError as error:
        print(str(error), file=sys.stderr)
        sys.exit(2)
