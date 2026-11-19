"""Staged primary bootstrap. Planning is not deployment authorization."""

import argparse
import base64
import datetime as dt
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
from primary_network import (DEFAULT_APP_ROLE, normalized_policy, observe_container_profiles, observe_edge,
                             observe_edge_monitoring, observe_policy, observe_private_service_endpoints, validate_network_manifest,
                             verify_vpn_identity, vpn_audience_body)
from shared_hub import (HUB_CONTRACT, PRIMARY_CONTRACT, latest_receipt, validate_hub_manifest,
                        validate_primary_manifest)


ROOT = Path(__file__).resolve().parents[2]
HUMAN_ROLES = ("PmsSpecialist", "MedicalSafetyReviewer", "RegulatoryAffairs", "QualityManager", "Auditor")
PHASES = ("identities", "network", "edge-bind", "foundation", "edge-policy", "grants", "initialize",
          "runtime", "runtime-policy", "seed", "publish-functions", "client-access", "activate")
NETWORK_PHASES = ("network", "edge-bind", "edge-policy", "runtime-policy", "client-access")
POLICY_PHASES = ("edge-policy", "runtime-policy", "client-access")
ARM_PHASES = (*NETWORK_PHASES, "foundation", "runtime", "activate")
EFFECTS = {
    "identities": ["CreateOwnedAppsServicePrincipalsAndGroups", "GrantOwnSpaDelegatedApiScope",
                   "AssignSelectedHumansToOwnBusinessRoles", "CreateOwnAssignmentRequiredVpnAudience",
                   "PreauthorizeMicrosoftRegisteredVpnClientForOwnScope", "AssignExactlyFourSelectedVpnUsers"],
    "network": ["CreateOnlyOwnResourceGroupsAndBlockedNetwork", "CreateOwnStandardFirewallAndVpnGw1AZ",
                "CreateOwnFirewallAndGatewayPublicIps", "KeepPointToSiteAuthenticationUnconfigured"],
    "edge-bind": ["BindActualOwnFirewallIpToRoutesAndDns", "LinkOwnDenyUnmatchedDnsPolicy",
                  "ConfigureOwnAssignmentRequiredEntraPointToSite", "CreateOwnPrivateOperator",
                  "ProvisionReviewedOptionalBastionAndLicensedClient"],
    "foundation": ["CreateOwnPrivateDataAndModelServices", "CreateOwnMonitorAndActionGroup",
                   "ConfigureOwnFirewallAndGatewayMetricsOnly"],
    "edge-policy": ["BindGeneratedPrivateServiceDns", "DenyOwnDataServiceEgressFallback",
                    "KeepPointToSiteApplicationAccessDenied"],
    "runtime-policy": ["BindActualRuntimeAndScmHostnames", "DenyRuntimePublicFallback",
                       "KeepPointToSiteApplicationAccessDenied"],
    "grants": ["GrantOwnRuntimeAndTemporaryBootstrapRights", "CreateOwnScopedDeadlineRole"],
    "initialize": ["BindPrivateDataHostsToNoPublicFallbackPolicy", "ImportBoundContainerImages",
                   "ApplyApplicationMigrationsAndSearchSchema", "InstallApprovedDeadlineExecutor"],
    "runtime": ["DeployBoundPrivateApiAgentHostAndFlexHosts"],
    "seed": ["SeedOnlySyntheticBaselineUsingBoundApiStartup"],
    "publish-functions": ["PublishBothBoundFlexPackagesUsingOneDeploy"],
    "client-access": ["AllowPointToSiteHttpsToActualOwnPrivateApiOnly", "KeepDirectDataAccessDenied"],
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

HUB_PHASES = ("identities", "network", "edge-bind", "hub-bindings")
HUB_EFFECTS = {
    "identities": ["CreateOnlyOwnSharedVpnAudienceAndServicePrincipal",
                   "AssignExactlyFourPreviouslyResolvedUsersWithoutNewDirectorySearch"],
    "network": ["CreateOnlyOwnSharedHubResourceGroupAndBlockedNetwork", "CreateOwnFirewallGatewayAndPrivateCertificateVault",
                "KeepPointToSiteAuthenticationUnconfigured", "DoNotRegisterSubscriptionFeatures"],
    "edge-bind": ["BindActualHubFirewallIpAndDns", "ConfigureSharedAssignmentRequiredFourUserPointToSite",
                  "KeepConsumerAndWebAccessUnconfigured"],
    "hub-bindings": ["ChangeOnlyReviewedConsumerReferences", "PreserveOtherConsumerAndOriginalBudgetDeadline",
                     "BindActualDnsAndPrivateHttpsBackends", "ReleaseOnlyExactOffboardedConsumerReferences",
                     "DeleteLastUnreferencedWafOnlyWhenNoRemainingWebConsumers"],
    "stop": ["RequireBothConsumersOffboarded", "DoNotInterruptAnotherConsumer"],
    "cleanup": ["RequireBothConsumersOffboardedAndReferencesAbsent",
                "DeleteOnlyOwnSharedHubAndOwnVpnDirectoryObjects", "RetainVaultSoftDeleteNoPurge"],
}


def phase_effects(manifest, phase):
    if manifest.get("contract") == HUB_CONTRACT:
        return HUB_EFFECTS[phase]
    if manifest.get("contract") == PRIMARY_CONTRACT:
        overrides = {
            "identities": ["CreateOnlyOwnBusinessAppsGroupsAndGrants", "DoNotChangeSharedVpnAudienceOrAssignments"],
            "network": ["CreateOnlyOwnPrimarySpokeAndOwnPeeringHalf", "ReferenceExactReadbackBoundHub",
                        "DoNotCreateHubOrSharedDnsResources"],
            "edge-bind": ["EnableOwnRemoteGatewayAfterHubTransitReadback", "CreateOwnPrivateOperator"],
            "edge-policy": ["VerifyHubOwnedDataDnsReceiptWithoutHubWrites"],
            "runtime-policy": ["VerifyActualPrivateWafBackendReceiptWithoutHubWrites"],
            "client-access": ["VerifyPrivateWafClientOriginAndKeepDirectBackendDataAccessDenied"],
            "cleanup": ["RequireHubOwnerOffboardingReceipt", "DeleteOnlyOwnPrimaryResourceGroupsAndBusinessDirectoryObjects"],
            "detach-hub": ["RequireOwnStoppedWorkAndCredentialRevocationEvidence", "DeleteOnlyOwnSpokePeeringHalf",
                           "KeepSharedHubVpnDnsWafAndOtherConsumerUnchanged"],
            "foundation": ["CreateOwnPrivateDataAndModelServices", "CreateOwnMonitorAndActionGroup",
                           "DoNotChangeHubMonitoringOrSharedDns"],
        }
        return overrides[phase] if phase in overrides else EFFECTS[phase]
    return EFFECTS[phase]


def is_arm_phase(manifest, phase):
    if manifest.get("contract") == HUB_CONTRACT:
        return phase in ("network", "edge-bind", "hub-bindings")
    return phase in ARM_PHASES and not (manifest.get("contract") == PRIMARY_CONTRACT and phase in POLICY_PHASES)


def phase_key(plan):
    return plan.get("operationPhase", plan["phase"])


def is_hub_offboarding(manifest, plan):
    return (manifest.get("contract") == HUB_CONTRACT and plan["phase"] == "hub-bindings"
            and plan["bindingInput"]["consumers"][plan["bindingInput"]["changedConsumer"]]["state"] == "offboarded")


def stable_id(manifest, name):
    return str(uuid.uuid5(uuid.UUID(manifest["deploymentId"]), name))


def load_manifest(path):
    manifest = read_json(path)
    if manifest.get("contract") not in ("cohors-primary-bootstrap-2", HUB_CONTRACT, PRIMARY_CONTRACT):
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
    if any(type(release.get(key)) is not int or release[key] <= 0
           for key in ("runId", "artifactId", "validationRunId", "applicationValidationRunId")):
        raise GateError("Hosted run and artifact IDs must come from actual completed outputs.")
    if any(type(release.get(key, 1)) is not int or release.get(key, 1) < 1
           for key in ("validationRunAttempt", "applicationValidationRunAttempt")):
        raise GateError("Reviewed run attempts must be positive exact integers; omitted legacy attempts mean one.")
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
    if manifest["contract"] == HUB_CONTRACT:
        validate_hub_manifest(manifest)
        return manifest
    for suffix in ("workload", "access"):
        if manifest["names"][suffix + "ResourceGroup"] != f"rg-{manifest['namePrefix']}-{suffix}":
            raise GateError("Resource group names must be generated within this primary deployment.")
    if manifest["contract"] == PRIMARY_CONTRACT:
        validate_primary_manifest(manifest)
    else:
        validate_network_manifest(manifest)
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
                   if path.is_file() and (path.suffix in (".bicep", ".py", ".sh", ".ps1", ".txt", ".json", ".yml", ".cs", ".csproj", ".kql")
                                          or path.name == ".gitattributes")
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
    if manifest.get("contract") == HUB_CONTRACT:
        return [f"/subscriptions/{subscription}/resourceGroups/{manifest['names']['hubResourceGroup']}"]
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
    if approval.get("phase") != plan["phase"] or approval.get("acknowledgedEffects") != phase_effects(manifest, plan["phase"]):
        raise GateError("Approval does not acknowledge the exact privileged effects.")
    if approval.get("reviewerObjectId") != manifest["binding"]["principalObjectId"]:
        raise GateError("Approval must identify the selected initial operator, not a substituted principal.")
    expires, reviewed = parse_time(approval["expiresAtUtc"]), parse_time(approval["reviewedAtUtc"])
    if not reviewed <= now < expires or expires - reviewed > dt.timedelta(hours=24):
        raise GateError("Phase approval is expired, future-dated, or exceeds twenty-four hours.")
    retirement = is_hub_offboarding(manifest, plan)
    if now >= parse_time(manifest["expiresAtUtc"]) and plan["phase"] not in ("stop", "cleanup", "detach-hub") and not retirement:
        raise GateError("Pilot deadline elapsed; only targeted stop/cleanup remains.")
    if approval.get("riskAcknowledged") is not True:
        raise GateError("The phase's concrete risk acknowledgement is missing.")
    for key in ("policyReviewSha256", "readinessEvidenceSha256", "costReviewSha256"):
        if not re.fullmatch(r"[0-9a-f]{64}", approval.get(key, "")):
            raise GateError(f"A real bound {key} is required.")
    if is_arm_phase(manifest, plan["phase"]):
        if not re.fullmatch(r"[0-9a-f]{64}", approval.get("whatIfSha256", "")):
            raise GateError("The additive what-if result must be separately reviewed and bound.")
    if manifest.get("contract") != HUB_CONTRACT and plan["phase"] in ("network", "edge-bind", "foundation") and manifest.get("containerAppsProfile") != "D4":
        raise GateError("Consumption requires AzurePlatformDNS and cannot use this strict-DNS candidate. D4 is the explicit working candidate, not an automatic fallback or cost/capacity approval.")
    if plan["phase"] in ("runtime", "runtime-policy", "seed", "publish-functions", "client-access", "activate"):
        contract = manifest.get("operationsSqlContract")
        if not contract or contract.get("applicationCommit") != manifest["releaseBundle"]["applicationCommit"]:
            raise GateError("A built, source-bound restricted Operations SQL contract is required. The b7 generic JSON writer is not eligible and cannot be accepted by a risk waiver.")
        raise GateError("The restricted Operations grant adapter is not yet hosted-qualified. The Entra/runtime activation executor remains closed; metadata cannot waive it.")


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
    if role == "vpn":
        body["api"] = vpn_audience_body(manifest, stable_id(manifest, "scope:p2s_access"))
        body["requiredResourceAccess"] = []
    if role == "spa":
        body["spa"] = {"redirectUris": []}
    return body


def ownership_tags(manifest, role):
    owner = "CohorsHub" if manifest.get("contract") == HUB_CONTRACT else "CohorsPrimary"
    return [f"{owner}:{manifest['deploymentId']}", f"CohorsBinding:{manifest['bindingHash']}",
            f"CohorsRole:{role}"]


class Bootstrap:
    def __init__(self, manifest, journal, client):
        self.manifest = manifest
        self.journal = journal
        self.client = client
        self.outputs = journal.state["outputs"]

    def effect(self, operation, method, url, body=None, *, allowed=(200, 201, 204), projection=None, headers=None):
        if not self.journal.begin(operation, method, url, body):
            return self.outputs[operation]
        result, response_headers, status = self.client.request(method, url, body, allowed=allowed,
                                                              **({"headers": headers} if headers else {}))
        if status == 202:
            location = response_headers.get("Azure-AsyncOperation") or response_headers.get("azure-asyncoperation") or response_headers.get("Location")
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
        if self.manifest.get("contract") == HUB_CONTRACT:
            return {"vpn": self.vpn_identity()}
        identities = {}
        selected_users = {actor["objectId"] for actor in self.manifest["actors"]} | set(
            self.manifest.get("vpnUserObjectIds", []))
        for identifier in sorted(selected_users):
            user = self.client.get(GRAPH + f"/v1.0/users/{identifier}?$select=id,accountEnabled,userType")
            if user.get("id") != identifier or user.get("accountEnabled") is not True:
                raise GateError("A selected existing actor is disabled or not verifiable; no directory mutation.")
        roles = ("api", "agents", "spa") if self.manifest.get("contract") == PRIMARY_CONTRACT else ("api", "agents", "spa", "vpn")
        for role in roles:
            application = self.owned_graph("applications", "app-" + role, app_body(self.manifest, role), role)
            sp_body = {"appId": application["clientId"], "displayName": application["displayName"],
                       "accountEnabled": True,
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
            for role in actor["roles"]:
                self.effect("member-" + role + "-" + actor["objectId"], "POST",
                            GRAPH + f"/v1.0/groups/{identities[role]['objectId']}/members/$ref",
                            {"@odata.id": GRAPH + "/v1.0/directoryObjects/" + actor["objectId"]}, allowed=(204,))
        if "vpn" in identities:
            self.assign_vpn_users(identities["vpn"])
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

    def assign_vpn_users(self, vpn):
        for identifier in self.manifest["vpnUserObjectIds"]:
            self.effect("vpn-assignment-" + identifier, "POST",
                        GRAPH + f"/v1.0/servicePrincipals/{vpn['servicePrincipalObjectId']}/appRoleAssignedTo",
                        {"principalId": identifier, "resourceId": vpn["servicePrincipalObjectId"],
                         "appRoleId": DEFAULT_APP_ROLE}, allowed=(201,),
                        projection=lambda value: {key: value[key] for key in
                                                  ("id", "principalId", "resourceId", "appRoleId", "principalType")})
        vpn["readback"] = verify_vpn_identity(self.client, self.manifest, vpn,
                                             stable_id(self.manifest, "scope:p2s_access"))

    def vpn_identity(self):
        application = self.owned_graph("applications", "app-vpn", app_body(self.manifest, "vpn"), "vpn")
        principal = self.owned_graph("servicePrincipals", "sp-vpn", {
            "appId": application["clientId"], "displayName": application["displayName"],
            "accountEnabled": True, "appRoleAssignmentRequired": True,
            "tags": ownership_tags(self.manifest, "vpn")}, "vpn")
        self.effect("identifier-vpn", "PATCH", GRAPH + "/v1.0/applications/" + application["objectId"],
                    {"identifierUris": ["api://" + application["clientId"]]})
        vpn = {**application, "servicePrincipalObjectId": principal["objectId"]}
        self.assign_vpn_users(vpn)
        return vpn

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


def phase_plan(manifest, phase, outputs, binding_input=None):
    phases = HUB_PHASES if manifest.get("contract") == HUB_CONTRACT else PHASES
    additional = ("detach-hub",) if manifest.get("contract") == PRIMARY_CONTRACT else ()
    if phase not in (*phases, *additional, "stop", "cleanup"):
        raise GateError("This phase is not part of the selected versioned owner/consumer contract.")
    if phase == "detach-hub" and "phase-stop" not in outputs:
        raise GateError("Own work must be stopped before detaching the Primary spoke.")
    if phase in phases and phase != phases[0]:
        previous = phases[phases.index(phase) - 1]
        if "phase-" + previous not in outputs:
            raise GateError(f"Phase {previous} must complete first; no prebuilt IDs may replace its outputs.")
    plan = {"contract": "cohors-primary-phase-plan-1", "bindingHash": manifest["bindingHash"],
            "phase": phase, "sourceFiles": source_binding(), "effects": phase_effects(manifest, phase),
            "ownedResourceGroups": own_group_ids(manifest), "priorOutputsHash": digest(outputs),
            "priorOutputHashes": {key: digest(value) for key, value in outputs.items()},
            "cloudMutationAuthorized": False, "createdAtUtc": timestamp()}
    if manifest.get("contract") == HUB_CONTRACT:
        from shared_hub_control import prepare_plan
        prepare_plan(plan, manifest, outputs, binding_input)
        return plan
    if binding_input is not None:
        raise GateError("Only the hub owner accepts a consumer binding snapshot.")
    if phase == "identities":
        shared = manifest.get("contract") == PRIMARY_CONTRACT
        plan["directoryChanges"] = {
            "applications": [app_body(manifest, role) for role in (("api", "agents", "spa") if shared else ("api", "agents", "spa", "vpn"))],
            "securityGroups": [manifest["namePrefix"] + "-" + role for role in ("sql-admin", *HUMAN_ROLES)],
            "actors": manifest["actors"], "delegatedConsent": {"scope": "access_as_user",
            "client": "new-own-spa", "resource": "new-own-api", "consentType": "AllPrincipals",
            "applicationAssignmentRequired": True},
            "vpnUserObjectIds": [] if shared else manifest["vpnUserObjectIds"],
            "vpnDefaultRoleOnly": not shared, "vpnAssignmentRequired": not shared,
            "noExistingAppCredentialsOrTenantSettingsChanged": True}
    if manifest.get("contract") == PRIMARY_CONTRACT and phase not in ("identities", "stop", "cleanup", "detach-hub"):
        receipt = latest_receipt(outputs)
        plan["hubReceiptSha256"] = receipt["receiptSha256"]
    if is_arm_phase(manifest, phase):
        plan["template"] = "primary-network.bicep" if phase in NETWORK_PHASES else "primary.bicep"
    return plan


def require_plan(plan, manifest, outputs):
    if plan.get("contract") != "cohors-primary-phase-plan-1" or plan.get("bindingHash") != manifest["bindingHash"]:
        raise GateError("Plan/manifest binding mismatch.")
    if plan["sourceFiles"] != source_binding():
        raise GateError("Source changed after phase planning.")
    if plan["effects"] != phase_effects(manifest, plan["phase"]) or plan["ownedResourceGroups"] != own_group_ids(manifest):
        raise GateError("Plan effects or ownership scope changed.")
    if any(key not in outputs or digest(outputs[key]) != expected
           for key, expected in plan["priorOutputHashes"].items()):
        raise GateError("A prerequisite output changed after phase planning.")
    # Completed effects of this same phase may exist on explicit resume, but no other phase may appear.
    phase = plan["phase"]
    if "phase-" + phase_key(plan) in outputs:
        raise GateError("This phase is already complete; inspect it rather than running it again.")
    if any(key.startswith("phase-") and key not in plan["priorOutputHashes"] for key in outputs):
        raise GateError("Another phase completed after this plan; create and review a new phase plan.")
    if manifest.get("contract") in (HUB_CONTRACT, PRIMARY_CONTRACT):
        if any(key.startswith("hub-receipt-") and key not in plan["priorOutputHashes"] for key in outputs):
            raise GateError("A newer hub receipt arrived after planning; replan rather than silently rebinding.")
        if manifest["contract"] == HUB_CONTRACT:
            from shared_hub_control import validate_plan
            validate_plan(plan, manifest, outputs)


def arm_parameters(manifest, phase, outputs, *, os_credentials=None, binding_input=None):
    if manifest.get("contract") == HUB_CONTRACT:
        from shared_hub_control import parameters
        return parameters(manifest, phase, outputs, binding_input)
    profile = {key: manifest[key] for key in (
        "bindingHash", "deploymentId", "expiresAtUtc", "names", "namePrefix", "location",
        "alertEmailReceivers", "network", "containerAppsProfile", "productGrants", "products")}
    profile["binding"] = {"tenantId": manifest["binding"]["tenantId"]}
    profile["contract"] = manifest.get("contract", "cohors-primary-bootstrap-2")
    profile["model"] = {key: manifest["model"][key] for key in
                        ("region", "enabled", "name", "version", "deploymentName", "capacity")}
    profile["access"] = {key: manifest["access"][key] for key in (
        "linuxImage", "linuxVmSize", "includeInteractiveClient", "windowsImage", "windowsVmSize",
        "clientOperatorObjectIds")}
    params = {"profile": {"value": profile}}
    if manifest.get("contract") == PRIMARY_CONTRACT and phase in ("network", "edge-bind"):
        receipt = latest_receipt(outputs, required_state="reserved" if phase == "network" else "peered")
        params.update({
            "topology": {"value": "shared-primary-v3"}, "hubReceipt": {"value": receipt},
            "bindManagedEdge": {"value": phase == "edge-bind"},
            "sshPublicKey": {"value": manifest["access"]["sshPublicKey"]},
            "windowsBootstrapPassword": {"value": (os_credentials or {}).get("windowsPassword", "")}})
    elif phase in NETWORK_PHASES:
        network = (None if phase == "network" else outputs.get(
            "phase-edge-bind" if phase in POLICY_PHASES else "phase-network"))
        platform = (outputs.get("phase-runtime") or outputs.get("phase-foundation") or {}).get("platform")
        params["sshPublicKey"] = {"value": manifest["access"]["sshPublicKey"]}
        params["windowsBootstrapPassword"] = {"value": (os_credentials or {}).get("windowsPassword", "")}
        params["vpnAudienceClientId"] = {"value": outputs["phase-identities"]["vpn"]["clientId"]}
        params["bindManagedEdge"] = {"value": phase != "network"}
        params["policyOnly"] = {"value": phase in POLICY_PHASES}
        params["precedingNetwork"] = {"value": network or {}}
        params["firewallPrivateIp"] = {"value": network["edge"]["firewallPrivateIp"] if network else ""}
        params["edgePolicy"] = {"value": normalized_policy(
            manifest, network, platform if phase in POLICY_PHASES else None,
            allow_clients=phase == "client-access")}
    else:
        params["identities"] = {"value": outputs["phase-identities"]}
        network = outputs["phase-edge-bind"]
        if manifest.get("contract") != PRIMARY_CONTRACT:
            names = {}
            for role, key in (("firewall", "firewallId"), ("gateway", "gatewayId")):
                identifier = network["edge"]["resources"][key]
                assert_owned_id(identifier, manifest)
                names[role] = identifier.rsplit("/", 1)[-1]
            network = {**network, "edge": {**network["edge"], "resourceNames": names}}
        params["networkPhase"] = {"value": network}
        params["deployRuntime"] = {"value": phase in ("runtime", "activate")}
        params["enableWatchdogAlert"] = {"value": phase == "activate"}
        params["release"] = {"value": outputs.get("phase-initialize", {}).get("release", {})}
    return params


def validate_what_if(value, manifest, *, removal_only=False):
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
        if removal_only and change["changeType"] == "Create":
            raise GateError("Consumer offboarding cannot create or recreate resources.")
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


def verify_network_checkpoint(client, manifest, outputs):
    if manifest.get("contract") in (HUB_CONTRACT, PRIMARY_CONTRACT):
        from shared_hub_control import verify_checkpoint
        return verify_checkpoint(client, manifest, outputs)
    for phase in reversed(NETWORK_PHASES):
        stage = outputs.get("phase-" + phase)
        if not stage:
            continue
        if not isinstance(stage.get("policyInput"), dict):
            raise GateError("The completed network checkpoint has no intact policy-input binding.")
        vpn = outputs["phase-identities"]["vpn"]
        verify_vpn_identity(client, manifest, vpn, stable_id(manifest, "scope:p2s_access"))
        observe_edge(client, manifest, stage, bound=phase != "network", identity=vpn)
        observe_policy(client, manifest, stage, stage["policyInput"], bound=phase != "network")
        platform = (outputs.get("phase-activate") or outputs.get("phase-runtime")
                    or outputs.get("phase-foundation"))
        if platform:
            observe_edge_monitoring(client, manifest, stage, platform)
        return
    raise GateError("No completed own network checkpoint exists; no inherited or guessed network state.")


def arm_apply(engine, phase, plan, approval, preview_path):
    manifest, client = engine.manifest, engine.client
    preview = read_json(preview_path)
    if file_hash(preview_path) != approval["whatIfSha256"] or preview.get("planHash") != digest(plan):
        raise GateError("Reviewed what-if file does not match the exact phase plan.")
    validate_what_if(preview["result"], manifest, removal_only=is_hub_offboarding(manifest, plan))
    operation = phase_key(plan)
    previous_arm = engine.outputs.get("arm-" + operation)
    if previous_arm:
        deployment = client.get(ARM + previous_arm["id"] + "?api-version=2025-04-01")
        if deployment["properties"].get("provisioningState") != "Succeeded":
            client.poll(ARM + previous_arm["id"] + "?api-version=2025-04-01")
            deployment = client.get(ARM + previous_arm["id"] + "?api-version=2025-04-01")
        if manifest.get("contract") == HUB_CONTRACT:
            expected = arm_parameters(manifest, phase, engine.outputs, binding_input=plan.get("bindingInput"))
            actual = deployment["properties"].get("parameters", {})
            if any(actual.get(key, {}).get("value") != value["value"] for key, value in expected.items()):
                raise GateError("The completed shared deployment is not this exact approved revision; no replay/adoption.")
        return {name: item["value"] for name, item in deployment["properties"]["outputs"].items()}
    check_owned_groups(client, manifest, must_exist=phase != "network")
    credentials = {"windowsPassword": secrets.token_urlsafe(40) + "aA1!"} if (
        phase == "edge-bind" and manifest.get("access", {}).get("includeInteractiveClient")) else {}
    template = compile_bicep(client, plan["template"])
    if digest(template) != preview["compiledTemplateHash"]:
        raise GateError("Compiled ARM differs from the preview.")
    parameters = arm_parameters(manifest, phase, engine.outputs, os_credentials=credentials,
                                binding_input=plan.get("bindingInput"))
    identifier = (f"/subscriptions/{manifest['binding']['subscriptionId']}/providers/Microsoft.Resources/"
                  f"deployments/{manifest['namePrefix']}-{operation}")
    url = ARM + identifier + "?api-version=2025-04-01"
    body = {"location": manifest["location"], "properties": {
        "mode": "Incremental", "template": template, "parameters": parameters}}
    engine.effect("arm-" + operation, "PUT", url, body, allowed=(200, 201, 202),
                  projection=lambda _: {"id": identifier})
    deployment = client.get(url)
    if deployment["properties"].get("provisioningState") != "Succeeded":
        client.poll(url)
        deployment = client.get(url)
    return {name: item["value"] for name, item in deployment["properties"]["outputs"].items()}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("plan", "inspect", "what-if", "apply", "reconcile", "bind-hub", "export-hub"))
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--journal", type=Path, required=True)
    parser.add_argument("--phase", choices=(*PHASES, "hub-bindings", "stop", "cleanup", "detach-hub"))
    parser.add_argument("--plan", type=Path)
    parser.add_argument("--approval", type=Path)
    parser.add_argument("--what-if", dest="preview", type=Path)
    parser.add_argument("--out", type=Path)
    parser.add_argument("--input", type=Path)
    parser.add_argument("--consumer", choices=("primary", "fabric"))
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
            plan = phase_plan(manifest, args.phase, journal.state["outputs"],
                              read_json(args.input) if args.input else None)
            write_json(args.out, plan)
            print("Phase plan prepared; no cloud access or deployment authorization.")
            return 0
        if args.command in ("bind-hub", "export-hub"):
            require_committed_controller(manifest)
            from shared_hub_control import exchange_receipt
            with bound_client(manifest["binding"], manifest["controlTokenBindings"]) as client:
                exchange_receipt(Bootstrap(manifest, journal, client), args)
            print("Read-only source-bound hub receipt exchange completed; no traffic or deployment acceptance.")
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
            if not is_arm_phase(manifest, plan["phase"]) or not args.out:
                raise GateError("Only ARM phases have what-if; directory effects use their concrete phase plan.")
            with bound_client(manifest["binding"], manifest["controlTokenBindings"], allow_writes=True) as client:
                check_owned_groups(client, manifest, must_exist=plan["phase"] != "network")
                if manifest.get("contract") == PRIMARY_CONTRACT:
                    verify_network_checkpoint(client, manifest, journal.state["outputs"])
                elif plan["phase"] == "network":
                    verify_vpn_identity(client, manifest, journal.state["outputs"]["phase-identities"]["vpn"],
                                        stable_id(manifest, "scope:p2s_access"))
                else:
                    verify_network_checkpoint(client, manifest, journal.state["outputs"])
                template = compile_bicep(client, plan["template"])
                parameters = arm_parameters(manifest, plan["phase"], journal.state["outputs"],
                                            os_credentials={"windowsPassword": secrets.token_urlsafe(40) + "aA1!"},
                                            binding_input=plan.get("bindingInput"))
                if manifest.get("contract") == HUB_CONTRACT:
                    from shared_hub_control import preflight
                    preflight(Bootstrap(manifest, journal, client), plan)
                path = (f"/subscriptions/{manifest['binding']['subscriptionId']}/providers/Microsoft.Resources/"
                        f"deployments/{manifest['namePrefix']}-{phase_key(plan)}/whatIf?api-version=2025-04-01")
                result, headers, status = client.request("POST", ARM + path, {
                    "location": manifest["location"], "properties": {"mode": "Incremental", "template": template,
                    "parameters": parameters, "whatIfSettings": {"resultFormat": "FullResourcePayloads"},
                    "validationLevel": "ProviderNoRbac"}}, allowed=(200, 202))
                if status == 202:
                    poll_url = headers.get("Location") or headers.get("location")
                    if not poll_url:
                        raise GateError("What-if polling reference is missing.")
                    result = client.poll(poll_url)
                validate_what_if(result, manifest, removal_only=is_hub_offboarding(manifest, plan))
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
        # Reference-only retirement must remain available when the original package expires.
        if phase not in ("stop", "cleanup", "detach-hub") and not is_hub_offboarding(manifest, plan):
            from primary_release import require_hosted_qualification
            require_hosted_qualification(manifest["releaseBundle"])
        with bound_client(manifest["binding"], manifest["controlTokenBindings"], allow_writes=True) as client:
            engine = Bootstrap(manifest, journal, client)
            if model_acceptance:
                from primary_release import verify_model
                fresh = verify_model(engine)
                expected = engine.outputs["phase-initialize"]["modelObservation"]
                if ({key: value for key, value in fresh.items() if key != "observedAtUtc"}
                        != {key: value for key, value in expected.items() if key != "observedAtUtc"}):
                    raise GateError("Model configuration changed since its bound live acceptance; no automatic upgrade/fallback.")
            resumed_hub_arm = manifest.get("contract") == HUB_CONTRACT and "arm-" + phase_key(plan) in engine.outputs
            if phase not in ("identities", "network", "stop", "cleanup", "detach-hub") and not resumed_hub_arm:
                verify_network_checkpoint(client, manifest, engine.outputs)
            if phase == "identities":
                result = engine.identities()
            elif is_arm_phase(manifest, phase):
                if not args.preview:
                    raise GateError("ARM apply requires the exact reviewed --what-if file.")
                if phase == "network" and manifest.get("contract") != PRIMARY_CONTRACT:
                    verify_vpn_identity(client, manifest, engine.outputs["phase-identities"]["vpn"],
                                        stable_id(manifest, "scope:p2s_access"))
                if manifest.get("contract") == HUB_CONTRACT:
                    from shared_hub_control import preflight
                    preflight(engine, plan, evidence_directory=args.approval.parent, approval=approval)
                elif manifest.get("contract") == PRIMARY_CONTRACT:
                    from shared_hub_control import require_primary_phase_receipt
                    require_primary_phase_receipt(engine, phase)
                result = arm_apply(engine, phase, plan, approval, args.preview)
            elif phase == "grants":
                result = engine.roles(engine.outputs["phase-foundation"])
            else:
                from primary_release import execute_phase
                if phase == "detach-hub":
                    from shared_hub_control import detach_primary
                    result = detach_primary(engine, approval, args.approval.parent)
                else:
                    result = execute_phase(engine, phase, approval)
            if is_arm_phase(manifest, phase) or phase in ("grants", "initialize", "seed", "publish-functions"):
                from primary_release import checkpoint_inventory
                if not resumed_hub_arm or "inventory-" + phase_key(plan) not in engine.outputs:
                    checkpoint_inventory(engine, phase_key(plan), result if phase in NETWORK_PHASES or phase == "hub-bindings" else None)
            if manifest.get("contract") == HUB_CONTRACT and is_arm_phase(manifest, phase):
                from shared_hub_control import complete_hub_phase
                complete_hub_phase(engine, plan, result, read_json(args.preview)["compiledTemplateHash"])
            elif manifest.get("contract") == PRIMARY_CONTRACT and phase in ("network", "edge-bind"):
                from shared_hub_control import observe_primary_spoke
                result["metadataReadback"] = observe_primary_spoke(client, manifest, result, latest_receipt(engine.outputs),
                                                                 remote_gateway=phase == "edge-bind")
            elif phase in NETWORK_PHASES and manifest.get("contract") != PRIMARY_CONTRACT:
                result["policyInput"] = arm_parameters(manifest, phase, engine.outputs)["edgePolicy"]["value"]
                result["metadataReadback"] = observe_edge(
                    client, manifest, result, bound=phase != "network",
                    identity=engine.outputs["phase-identities"]["vpn"])
                result["policyReadback"] = observe_policy(
                    client, manifest, result, result["policyInput"],
                    bound=phase != "network")
            if phase in ("foundation", "runtime", "activate"):
                result["privateServiceEndpointReadback"] = observe_private_service_endpoints(client, manifest, result["platform"])
                result["containerProfileReadback"] = observe_container_profiles(
                    client, manifest, result, runtime=phase != "foundation")
                if manifest.get("contract") != PRIMARY_CONTRACT:
                    result["edgeMonitoringReadback"] = observe_edge_monitoring(
                        client, manifest, engine.outputs["phase-edge-bind"], result)
            if phase == "activate" and manifest.get("contract") == PRIMARY_CONTRACT:
                from shared_hub_control import bind_spa_origin
                bind_spa_origin(engine)
            if model_acceptance:
                result["modelAcceptance"] = model_acceptance
            operation = "phase-" + phase_key(plan)
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
