"""Read-only, target-bound capability and policy evidence. Never grants create permission."""

import argparse
import ipaddress
from pathlib import Path
import re
import sys
import urllib.parse

from primary_control import (ARM, GRAPH, GateError, bound_client, digest, file_hash, read_json,
                             require_guid, timestamp, write_json)


PROVIDER_VERSION = "2021-04-01"
POLICY_VERSION = "2023-04-01"
MODEL_REVIEW_REGIONS = ("francecentral", "swedencentral", "westeurope", "northeurope", "germanywestcentral")


def api(path, version, **query):
    return ARM + path + "?" + urllib.parse.urlencode({"api-version": version, **query})


def resource_contracts(root):
    required = {}
    for file in sorted(Path(root).rglob("*.bicep")):
        if "fabric" in file.parts:
            continue
        for resource_type, version in re.findall(
                r"resource\s+\w+\s+'(Microsoft\.[^'@]+)@([^']+)'", file.read_text(encoding="utf-8-sig")):
            provider, kind = resource_type.split("/", 1)
            required.setdefault(provider, {}).setdefault(kind, set()).add(version)
    return required


def policy_type_hints(value):
    found = set()
    if isinstance(value, dict):
        if value.get("field", "").lower() == "type":
            possible = value.get("equals", value.get("in", []))
            for entry in possible if isinstance(possible, list) else [possible]:
                if isinstance(entry, str) and entry.lower().startswith("microsoft."):
                    found.add(entry.lower())
        for child in value.values():
            found.update(policy_type_hints(child))
    elif isinstance(value, list):
        for child in value:
            found.update(policy_type_hints(child))
    return found


def model_candidate(row, location):
    model = row.get("model", {})
    capabilities = model.get("capabilities", {})
    if str(capabilities.get("chatCompletion", "")).lower() != "true":
        return None
    skus = model.get("skus", [])
    return {
        "region": location, "accountKind": row.get("kind"), "accountSku": row.get("skuName"),
        "model": model,
        "regionalStandardCatalogued": any(sku.get("name") == "Standard" for sku in skus),
        "provisionedManagedPolicyBlocked": any(sku.get("name") == "ProvisionedManaged" for sku in skus),
        "currentOpenAiChatContractCandidate": model.get("format") == "OpenAI",
        "clinicalQualityProven": False, "deployableCapacityProven": False,
    }


def collect_model_candidates(client, request, locations, output):
    if not locations or len(locations) != len(set(locations)) or any(
            location not in MODEL_REVIEW_REGIONS for location in locations):
        raise GateError("Model review must name distinct supported EU-member-state candidate regions.")
    binding = request["binding"]
    output["currentContext"] = client.credential.check_context()
    client.credential.token(ARM + "/")
    client.credential.token(GRAPH + "/")
    if client.get(GRAPH + "/v1.0/me?$select=id").get("id") != binding["principalObjectId"]:
        raise GateError("Graph /me does not match the selected initial deployer.")
    scope = "/subscriptions/" + binding["subscriptionId"]
    output["regions"] = {}
    for location in locations:
        rows = client.collection(api(
            scope + f"/providers/Microsoft.CognitiveServices/locations/{location}/models", "2025-06-01"))
        output["regions"][location] = {
            "candidates": [candidate for row in rows if (candidate := model_candidate(row, location))],
            "quota": client.collection(api(
                scope + f"/providers/Microsoft.CognitiveServices/locations/{location}/usages", "2025-06-01")),
        }
    output["modelSelection"] = "NotSelected; expert task evaluation precedes live activation, not evaluation-resource preparation."
    output["regionSelection"] = "FranceCentralRemainsWorkingProfile; comparison does not authorize another deployment region."
    output["qualityPriority"] = "ClinicalAnalysisAndAssuranceBeforePrice; no automatic small-model fallback."
    output["status"] = "ObservedNotCreateAuthorized"


def collect_policies(client, scope, bom, result):
    assignments = client.collection(api(scope + "/providers/Microsoft.Authorization/policyAssignments",
                                        POLICY_VERSION, **{"$filter": "atScope()"}))
    definitions = result.setdefault("definitions", {})

    def definition(identifier):
        if identifier in definitions:
            return
        result["pendingDefinition"] = identifier
        if not re.fullmatch(r"/(?:subscriptions/[^/]+/|providers/Microsoft\.Management/managementGroups/[^/]+/)?"
                            r"providers/Microsoft\.Authorization/policy(?:Set)?Definitions/[^/?#]+"
                            r"(?:/versions/[0-9.]+)?", identifier, flags=re.IGNORECASE):
            raise GateError("Unexpected policy definition reference.")
        data = client.get(api(identifier, POLICY_VERSION))
        properties = data.get("properties", {})
        projected = {key: properties.get(key) for key in
                     ("displayName", "policyType", "mode", "parameters", "policyRule", "policyDefinitions")}
        hints = policy_type_hints(properties.get("policyRule", {}))
        projected["literalTypeHints"] = sorted(hints)
        projected["bomTriage"] = ("PotentiallyApplicableNeedsWhatIf" if not hints or any(
            any(kind == hint or kind.startswith(hint + "/") for kind in bom) for hint in hints)
                                 else "NoLiteralBomTypeMatchNotAnExemption")
        definitions[identifier] = projected
        for member in properties.get("policyDefinitions", []):
            definition(member["policyDefinitionId"])

    result["assignments"] = []
    for assignment in assignments:
        properties = assignment.get("properties", {})
        result["assignments"].append({"id": assignment["id"], "name": assignment["name"],
                       **{key: properties.get(key) for key in
                          ("scope", "notScopes", "enforcementMode", "parameters", "policyDefinitionId",
                           "overrides", "resourceSelectors")}})
        definition(properties["policyDefinitionId"])
    result.pop("pendingDefinition", None)
    result["interpretation"] = ("Literal-type triage only. Conditions, aliases, exemptions, MFA and dynamic "
                                "parameters still require effective provider/what-if and authorized create evaluation.")


def collect(client, request, infra_root, output):
    binding = request["binding"]
    for key in ("tenantId", "subscriptionId", "principalObjectId", "oauthClientId"):
        require_guid(binding[key], key)
    scope = "/subscriptions/" + binding["subscriptionId"]
    location = request["location"]
    if location != "francecentral":
        raise GateError("This primary candidate is bound to France Central.")
    output["currentContext"] = client.credential.check_context()
    client.credential.token(ARM + "/")
    client.credential.token(GRAPH + "/")
    me = client.get(GRAPH + "/v1.0/me?$select=id")
    if me.get("id") != binding["principalObjectId"]:
        raise GateError("Graph /me does not match the selected initial deployer.")
    output["principalConfirmed"] = True
    contracts = resource_contracts(infra_root)
    # Candidate access components are not part of the historical R3 modules.
    contracts.setdefault("Microsoft.Compute", {}).update({
        "virtualMachines": {"2024-11-01"}, "virtualMachines/runCommands": {"2024-11-01"}})
    contracts.setdefault("Microsoft.Network", {}).update({
        "bastionHosts": {"2024-05-01"}, "routeTables": {"2024-05-01"}})
    output.setdefault("providers", {})
    for provider, kinds in sorted(contracts.items()):
        if provider in output["providers"]:
            continue
        data = client.get(api(scope + "/providers/" + provider, PROVIDER_VERSION))
        rows = {row["resourceType"].lower(): row for row in data.get("resourceTypes", [])}
        output["providers"][provider] = {
            "registrationState": data.get("registrationState"),
            "resources": {kind: {
                "resourceTypeListed": kind.lower() in rows,
                "versions": {version: (version in rows[kind.lower()].get("apiVersions", [])
                                      if kind.lower() in rows else None)
                             for version in sorted(versions)},
                "locations": rows.get(kind.lower(), {}).get("locations", []),
            } for kind, versions in sorted(kinds.items())}}
    bom = {f"{provider}/{kind}".lower() for provider, kinds in contracts.items() for kind in kinds}
    output.setdefault("policy", {})
    collect_policies(client, scope, bom, output["policy"])
    output["permissions"] = client.collection(api(
        scope + "/providers/Microsoft.Authorization/permissions", "2022-04-01"))
    output["denyAssignments"] = client.collection(api(
        scope + "/providers/Microsoft.Authorization/denyAssignments", "2022-04-01",
        **{"$filter": "atScope()"}))
    output["locks"] = client.collection(api(
        scope + "/providers/Microsoft.Authorization/locks", "2016-09-01",
        **{"$filter": "atScope()"}))
    networks = client.collection(api(scope + "/providers/Microsoft.Network/virtualNetworks", "2024-05-01"))
    desired = ipaddress.ip_network(request["vnetCidr"], strict=True)
    output["cidrReview"] = {"candidate": str(desired), "subscriptionVnets": [],
                            "peeredOrExternalNetworksVerified": False}
    for network in networks:
        prefixes = network.get("properties", {}).get("addressSpace", {}).get("addressPrefixes", [])
        output["cidrReview"]["subscriptionVnets"].append({
            "resourceIdSha256": digest(network["id"]), "prefixes": prefixes,
            "overlaps": any(desired.version == ipaddress.ip_network(prefix).version
                            and desired.overlaps(ipaddress.ip_network(prefix)) for prefix in prefixes)})
    skus = client.collection(api(scope + "/providers/Microsoft.Compute/skus", "2021-07-01",
                                 **{"$filter": "location eq 'francecentral'"}))
    output["computeSkus"] = [row for row in skus if row.get("resourceType") == "virtualMachines"
                             and row.get("name") in request["computeCandidates"]]
    output["computeUsage"] = client.collection(api(
        scope + f"/providers/Microsoft.Compute/locations/{location}/usages", "2024-07-01"))
    output["networkUsage"] = client.collection(api(
        scope + f"/providers/Microsoft.Network/locations/{location}/usages", "2024-05-01"))
    output["modelCatalog"] = []
    models = client.collection(api(
        scope + f"/providers/Microsoft.CognitiveServices/locations/{location}/models", "2025-06-01"))
    for row in models:
        model = row.get("model", row)
        if model.get("name") in request["modelCandidates"]:
            output["modelCatalog"].append(row)
    output["modelUsages"] = client.collection(api(
        scope + f"/providers/Microsoft.CognitiveServices/locations/{location}/usages", "2025-06-01"))
    output["modelSelection"] = "NotSelected; catalog/quota metadata is not capacity, model-boundary or create proof."
    output["images"] = {}
    for image in request["imageCandidates"]:
        path = (scope + f"/providers/Microsoft.Compute/locations/{location}/publishers/{image['publisher']}"
                f"/artifacttypes/vmimage/offers/{image['offer']}/skus/{image['sku']}/versions")
        versions = client.get(api(path, "2024-07-01", **{"$top": "5", "$orderby": "name desc"}))
        output["images"][image["role"]] = {"reference": image, "versions": versions}
    output["status"] = "ObservedNotCreateAuthorized"


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--request", required=True, type=Path)
    parser.add_argument("--out", required=True, type=Path)
    parser.add_argument("--previous", type=Path, help="Reuse a prior bound partial observation, never overwrite it.")
    parser.add_argument("--models-only", action="store_true", help="Only fresh bound model catalogs/quotas; no repeated inventory.")
    parser.add_argument("--model-region", action="append", choices=MODEL_REVIEW_REGIONS)
    args = parser.parse_args(argv)
    if args.out.exists():
        raise GateError("Evidence already exists; no implicit cloud retry.")
    request = read_json(args.request)
    evidence = {"contract": "cohors-primary-readiness-1", "startedAtUtc": timestamp(),
                "requestSha256": file_hash(args.request), "binding": request["binding"],
                "cloudMutations": 0, "createAuthorizationProven": False}
    if args.model_region and not args.models_only:
        raise GateError("--model-region requires the explicit read-only --models-only mode.")
    if args.models_only:
        if args.previous:
            raise GateError("A model comparison is a new bounded observation, not an inventory retry.")
        evidence["contract"] = "cohors-primary-model-observation-1"
        evidence["requestedRegions"] = args.model_region or [request["location"]]
    if args.previous:
        previous = read_json(args.previous)
        if previous.get("requestSha256") != evidence["requestSha256"] or previous.get("binding") != request["binding"]:
            raise GateError("Previous observation is bound to another request/context.")
        evidence["previousObservationSha256"] = file_hash(args.previous)
        for key in ("providers", "policy"):
            if key in previous:
                evidence[key] = previous[key]
    failed = False
    with bound_client(request["binding"], request.get("controlTokenBindings")) as client:
        try:
            if args.models_only:
                collect_model_candidates(client, request, evidence["requestedRegions"], evidence)
            else:
                collect(client, request, Path(__file__).parents[1], evidence)
        except GateError as error:
            evidence["status"] = "BlockedNoRetry"
            evidence["blocker"] = str(error)
            failed = True
        finally:
            evidence["tokenMetadata"] = client.credential.metadata
            evidence["requests"] = client.observations
            evidence["completedAtUtc"] = timestamp()
            write_json(args.out, evidence)
    print(f"{evidence['status']}; {len(evidence['requests'])} bounded requests; SHA256 {file_hash(args.out)}")
    return 2 if failed else 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except GateError as error:
        print(str(error), file=sys.stderr)
        sys.exit(2)
