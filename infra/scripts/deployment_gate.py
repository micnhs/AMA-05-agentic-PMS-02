"""Offline, fail-closed validation of reviewed deployment inputs. Never calls Azure."""

import argparse
import hashlib
import ipaddress
import json
import re
from datetime import datetime, timedelta, timezone
from pathlib import Path
from uuid import UUID


EU_CANDIDATES = {"westeurope", "northeurope", "swedencentral", "germanywestcentral", "francecentral"}
EU_COUNTRIES = set("AT BE BG HR CY CZ DK EE FI FR DE GR HU IE IT LV LT LU MT NL PL PT RO SK SI ES SE".split())
REQUIRED_GATES = {
    "subscriptionAndProviders", "regionSkuQuotaCost", "euProcessing", "networkDnsPrivateRunner",
    "identityAndAuthorization", "telemetryAndWatchdog", "backupRestoreLedger",
    "sqlProtectionBaseline", "sqlAuditDataClassification",
}
SQL_THREAT_GATES = {
    "sqlThreatProtectionCost", "sqlThreatProtectionPrivacy", "sqlThreatProtectionEu",
    "sqlThreatProtectionAccessRetention", "sqlThreatProtectionSecOps",
}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def real_uuid(value):
    try:
        return UUID(value).int != 0
    except (ValueError, TypeError, AttributeError):
        return False


def validate(document, approval, context, parameters_hash, now):
    require(approval.get("schemaVersion") == 1, "Unsupported approval schema.")
    require(re.fullmatch(r"[0-9a-f]{40}", context["sourceSha"]) is not None, "An immutable 40-character source SHA is required.")
    require(approval.get("reviewId") == context["reviewId"] and context["reviewId"].strip(), "Review ID does not match.")
    require(approval.get("parametersSha256") == parameters_hash, "Reviewed parameter digest does not match.")
    expiry = datetime.fromisoformat(approval.get("expiresUtc", "").replace("Z", "+00:00"))
    require(expiry.tzinfo is not None and expiry > now, "Deployment review expired or lacks a timezone.")
    require(expiry - now <= timedelta(days=14), "Approval validity must be at most 14 days.")
    for key in ("tenantId", "subscriptionId"):
        require(real_uuid(context[key]), f"Valid {key} is required.")
        require(approval.get(key) == context[key], f"Approval {key} does not match the protected environment.")
    require(approval.get("resourceGroup") == context["resourceGroup"], "Resource group is outside the reviewed boundary.")
    require(re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.()-]{0,89}", context["resourceGroup"]) is not None, "Invalid resource group name.")

    raw = json.dumps(document) + json.dumps(approval)
    require(not re.search(r"REPLACE|PLACEHOLDER|00000000-0000-0000-0000-000000000000", raw, re.I), "Example/placeholder values cannot be deployed.")
    params = document.get("parameters", {})
    require(params and all(isinstance(item, dict) and set(item) == {"value"} for item in params.values()), "Only explicit, non-secret ARM parameter values are accepted.")
    values = {key: item["value"] for key, item in params.items()}
    for key in ("deployRuntime", "deployModel", "enableWatchdogAlert", "enableDigestPublisherGrant", "deploySqlThreatProtection"):
        require(key not in values or type(values[key]) is bool, f"{key} must be a JSON boolean.")
    require(values.get("location") in EU_CANDIDATES, "Location is not an approved EU-member-state candidate.")
    require(approval.get("location") == values["location"], "Location differs from the review.")
    require(re.fullmatch(r"[a-z][a-z0-9]{2,9}", values.get("namePrefix", "cohors")) is not None, "Invalid workload naming prefix.")
    require(real_uuid(values.get("sqlAdminGroupObjectId")), "A real SQL administrator group object ID is required.")
    require(values.get("sqlAdminGroupName", "").strip(), "SQL administrator group name is required.")
    require(values.get("entraTenantId", context["tenantId"]) == context["tenantId"], "Application tenant and deployment tenant differ.")
    for key in ("egressRouteTableResourceId", "actionGroupResourceId"):
        resource_id = values.get(key, "")
        require(resource_id.lower().startswith(f"/subscriptions/{context['subscriptionId'].lower()}/resourcegroups/"), f"{key} must reference a reviewed resource in this subscription.")
    require("/providers/microsoft.network/routetables/" in values["egressRouteTableResourceId"].lower(), "A route table resource ID is required.")
    require("/providers/microsoft.insights/actiongroups/" in values["actionGroupResourceId"].lower(), "An action group resource ID is required.")

    parent = ipaddress.ip_network(values.get("vnetPrefix", "10.42.0.0/16"))
    require(parent.version == 4 and parent.is_private, "A private IPv4 VNet allocation is required.")
    ranges = []
    for key, default, largest_prefix in (
        ("containerAppsSubnetPrefix", "10.42.0.0/23", 23),
        ("workflowsSubnetPrefix", "10.42.2.0/26", 26),
        ("operationsSubnetPrefix", "10.42.2.64/26", 26),
        ("privateEndpointsSubnetPrefix", "10.42.3.0/24", 24),
    ):
        subnet = ipaddress.ip_network(values.get(key, default))
        require(subnet.subnet_of(parent) and subnet.prefixlen <= largest_prefix, f"{key} is outside the VNet or too small.")
        require(not any(subnet.overlaps(other) for other in ranges), "Workload subnets overlap.")
        ranges.append(subnet)

    gates = approval.get("gates", {})
    required = set(REQUIRED_GATES)
    if values.get("deploySqlThreatProtection"):
        required.update(SQL_THREAT_GATES)
        recipients = values.get("sqlSecurityNotificationEmails", [])
        require(isinstance(recipients, list) and recipients and all(
            isinstance(address, str) and re.fullmatch(r"[^@\s<>]+@[^@\s<>]+\.[^@\s<>]+", address)
            for address in recipients
        ), "Threat protection requires explicit reviewed SecOps notification recipients.")
    if values.get("deployModel"):
        required.add("modelVersionCapacityGeography")
        require(values.get("modelName", "").strip() and values.get("modelVersion", "").strip(), "Model name and explicit version are required.")
        require(type(values.get("modelCapacity")) is int and values["modelCapacity"] > 0, "Model-specific capacity must be explicitly reviewed.")
        review = values.get("foundryBoundaryReview", {})
        require(isinstance(review, dict), "foundryBoundaryReview must be an object.")
        require(review.get("deploymentSku") == "Standard", "Global, DataZone and Developer deployments are not approved.")
        require(review.get("deploymentRegion") == values["location"] and review.get("deploymentGeography", "").strip(), "Actual model deployment region and geography must be recorded.")
        require(review.get("reference", "").strip(), "The model boundary review reference is required.")
        require(re.fullmatch(r"[0-9a-f]{64}", review.get("configurationHash", "")) is not None, "Use the AgentHost canonical boundary hash; a boolean approval is insufficient.")
        countries = review.get("processingCountries", [])
        require(isinstance(countries, list) and countries and all(country in EU_COUNTRIES for country in countries), "The processing-country list must be explicit and EU-member-state-only.")
    if values.get("deployRuntime"):
        required.add("runtimeArtifactsAndBootstrap")
        require(values.get("deployModel") is True, "Runtime release must preserve an explicitly configured model deployment.")
        require(values.get("enableWatchdogAlert") is True, "Runtime deployment requires the independent missing-success alert.")
        for key in ("apiImageDigest", "agentsImageDigest"):
            digest = values.get(key, "")
            require(re.fullmatch(r"[0-9a-f]{64}", digest) is not None and len(set(digest)) > 1, f"{key} must be an immutable, reviewed image digest.")
        require(real_uuid(values.get("spaClientId")), "SPA client ID is required.")
        for key in ("apiAudience", "agentsAudience"):
            require(real_uuid(values.get(key, "").removeprefix("api://")), f"{key} must identify an Entra app.")
        require(values["apiAudience"].removeprefix("api://").lower() != values["agentsAudience"].removeprefix("api://").lower(), "API and AgentHost must use separate audiences.")
        for key in ("workflowApiScope", "agentsApiScope"):
            require(re.fullmatch(r"api://[0-9a-fA-F-]{36}/\.default", values.get(key, "")) is not None, f"{key} must be an application .default scope.")
        require(values["workflowApiScope"] == f"api://{values['apiAudience'].removeprefix('api://')}/.default", "Workflow scope targets a different API audience.")
        require(values["agentsApiScope"] == f"api://{values['agentsAudience'].removeprefix('api://')}/.default", "Agent scope targets a different AgentHost audience.")
        require(values.get("spaDelegatedScope") == f"api://{values['apiAudience'].removeprefix('api://')}/access_as_user", "SPA delegated scope must target the Cohors API.")
        require(values.get("operationsFunctionName", "DeadlineWatchdog") == "DeadlineWatchdog", "Operations scaling group must match the confirmed DeadlineWatchdog trigger.")
        products = values.get("agentProducts", [])
        require(isinstance(products, list) and products and all(isinstance(product, str) and re.fullmatch(r"[A-Za-z0-9_-]{1,64}", product) for product in products), "AgentHost requires an explicit product allowlist.")
    if values.get("enableDigestPublisherGrant"):
        required.add("independentDigestPublisher")
    require(values.get("operationsTelemetryRole", "cohors-operations") == "cohors-operations", "Telemetry role must match the confirmed Operations emitter.")
    for object_id, products in values.get("productGrants", {}).items():
        require(real_uuid(object_id) and isinstance(products, list) and products, "Human product grants require a real object ID and nonempty product list.")
        require(all(isinstance(product, str) and re.fullmatch(r"[A-Za-z0-9_-]{1,64}", product) for product in products), "Invalid product identifier.")
    for key in required:
        gate = gates.get(key, {})
        require(gate.get("approved") is True and bool(gate.get("evidence", "").strip()), f"Missing explicit review and evidence: {key}")
    return values


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--parameters", required=True, type=Path)
    parser.add_argument("--approval", required=True, type=Path)
    parser.add_argument("--context", required=True, type=Path)
    args = parser.parse_args()
    data = args.parameters.read_bytes()
    try:
        validate(json.loads(data), json.loads(args.approval.read_text(encoding="utf-8-sig")),
                 json.loads(args.context.read_text(encoding="utf-8-sig")),
                 hashlib.sha256(data).hexdigest(), datetime.now(timezone.utc))
    except (ValueError, TypeError, KeyError) as error:
        parser.exit(1, f"Deployment gate refused: {error}\n")
    print("Offline deployment input gates passed; this is not cloud availability, permission or operational validation.")
