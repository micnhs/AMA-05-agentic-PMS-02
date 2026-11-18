"""Export every compiled ARM template for Checkov's legacy resource-list reader.

These scan-only files are not deployment artifacts. Source-proven SQL audit
relationships and both branches of a narrowly supported DHCP object expression
are projected. Resource conditions, copies and unresolved DNS addresses remain.
"""

import argparse
import copy
import hashlib
import itertools
import json
import re
from pathlib import Path


SQL_SERVER = "microsoft.sql/servers"
SQL_AUDIT = SQL_SERVER + "/auditingsettings"
STORAGE = "microsoft.storage/storageaccounts"
# Checkov 3.4.5 StorageAccountName returns UNKNOWN for these substrings.
IGNORED_STORAGE_NAMES = ("local.", "module.", "var.", "random_string.", "random_id.",
                         "random_integer.", "random_pet.", "azurecaf_name", "each.", "substring")


def digest(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, ensure_ascii=True,
                                     separators=(",", ":")).encode("utf-8")).hexdigest()


def sql_name_expression(value):
    """Compare SQL parent-name syntax without evaluating parameters or functions."""
    if not isinstance(value, str):
        raise ValueError("A resource name must be a string.")
    if not value.startswith("[") or not value.endswith("]"):
        return ("literal", value)
    if value.startswith("[["):
        return ("literal", value[1:])
    text = value[1:-1]
    token = re.compile(r"\s*(?:'((?:[^']|'')*)'|([a-zA-Z][a-zA-Z0-9]*)|([(),]))")
    position = 0

    def read():
        nonlocal position
        match = token.match(text, position)
        if match is None:
            raise ValueError("Unsupported ARM naming expression.")
        position = match.end()
        if match[1] is not None:
            return ("literal", match[1].replace("''", "'"))
        return ("word", match[2].lower()) if match[2] else ("punctuation", match[3])

    def parse(depth=0):
        nonlocal position
        if depth > 32:
            raise ValueError("ARM naming expression is too deep.")
        kind, item = read()
        if kind == "literal":
            return (kind, item)
        if kind != "word" or item not in ("parameters", "variables", "format"):
            raise ValueError(f"Unsupported ARM naming function: {item}")
        if read() != ("punctuation", "("):
            raise ValueError("Malformed ARM naming function.")
        arguments = [parse(depth + 1)]
        while True:
            separator = read()
            if separator == ("punctuation", ")"):
                break
            if separator != ("punctuation", ","):
                raise ValueError("Malformed ARM naming arguments.")
            arguments.append(parse(depth + 1))
        if (arguments[0][0] != "literal" or
                (item == "format" and len(arguments) < 2) or
                (item != "format" and len(arguments) != 1)):
            raise ValueError("Unsupported ARM naming arguments.")
        return (item, tuple(arguments))

    result = parse()
    if text[position:].strip():
        raise ValueError("Unsupported trailing ARM naming expression.")
    return result


def audit_parent(name):
    node = sql_name_expression(name)
    if node[0] == "literal":
        parts = node[1].split("/")
        if len(parts) == 2 and parts[1] == "default":
            return ("literal", parts[0])
    elif node[0] == "format":
        arguments = node[1]
        if len(arguments) == 3 and arguments[0] == ("literal", "{0}/{1}") and arguments[2] == ("literal", "default"):
            return arguments[1]
        if len(arguments) == 2 and arguments[0] == ("literal", "{0}/default"):
            return arguments[1]
    raise ValueError("Unsupported SQL auditingSettings parent-name expression.")


def project_sql_audit(entries, path, changes):
    servers = [(symbol, resource) for symbol, resource in entries if resource["type"].lower() == SQL_SERVER]
    projected = set()
    for symbol, audit in entries:
        if audit["type"].lower() != SQL_AUDIT:
            continue
        owner_name = audit_parent(audit.get("name"))
        owners = [(key, server) for key, server in servers if sql_name_expression(server.get("name")) == owner_name]
        if len(owners) != 1:
            raise ValueError("SQL auditingSettings must match exactly one server in the same template.")
        owner, server = owners[0]
        if owner in projected or any(key in resource for resource in (server, audit) for key in ("copy", "scope")):
            raise ValueError("Ambiguous SQL audit copy/scope or multiple policies; no relationship fallback.")
        if server.get("existing", False) or audit.get("existing", False):
            raise ValueError("Existing SQL resources do not prove a managed auditing policy.")
        audit_condition = audit.get("condition", True)
        if audit_condition is not True and digest(audit_condition) != digest(server.get("condition", True)):
            raise ValueError("SQL audit condition does not cover the server condition.")
        children = server.get("resources", [])
        if not isinstance(children, list) or any(
            not isinstance(child, dict) or not isinstance(child.get("type"), str) or
            child["type"].lower().endswith("auditingsettings")
            for child in children
        ):
            raise ValueError("Conflicting or unsupported embedded SQL auditingSettings.")
        # Keep the standalone resource; this exact duplicate exposes its relationship.
        server["resources"] = [*children, copy.deepcopy(audit)]
        projected.add(owner)
        changes.append({"check": "CKV_AZURE_23", "templatePath": list(path),
                        "resource": str(symbol), "parent": str(owner), "resourceSha256": digest(audit)})


def scan_copy(document):
    result = copy.deepcopy(document)
    changes = []

    def visit(template, path=()):
        resources = template["resources"]
        entries = list(resources.items() if isinstance(resources, dict) else enumerate(resources))
        project_sql_audit(entries, path, changes)
        for symbol, resource in entries:
            kind = resource["type"].lower()
            if kind == STORAGE:
                if any(marker in str(resource.get("name", "")) for marker in IGNORED_STORAGE_NAMES):
                    raise ValueError("Checkov may ignore this Storage name instead of checking it.")
            elif kind == "microsoft.resources/deployments":
                visit(resource["properties"]["template"], path + (str(symbol),))
        template["resources"] = [resource for _, resource in entries]

    visit(result)
    verify_projection(document, result, changes)
    return result, changes


def verify_projection(document, projected, changes):
    """Undo only the recorded projections, then compare the entire resource graph."""
    restored = copy.deepcopy(projected)
    source_templates, scan_templates = list(templates(document)), list(templates(restored))
    if len(source_templates) != len(scan_templates):
        raise ValueError("Scan normalization changed template coverage.")
    by_path = {}
    for (path, original), (_, scan) in zip(source_templates, scan_templates):
        resources = original["resources"]
        entries = list(resources.items() if isinstance(resources, dict) else enumerate(resources))
        if not isinstance(scan["resources"], list) or len(entries) != len(scan["resources"]):
            raise ValueError("Scan normalization changed resource coverage.")
        by_path[path] = {str(symbol): (source, target)
                         for (symbol, source), target in zip(entries, scan["resources"])}
    for change in reversed(changes):
        if change["check"] != "CKV_AZURE_23":
            raise ValueError("Unsupported scan normalization.")
        resources = by_path[tuple(change["templatePath"])]
        original, _ = resources[change["resource"]]
        if digest(original) != change["resourceSha256"]:
            raise ValueError("Normalization is not bound to its original resource.")
        parent, scan_parent = resources[change["parent"]]
        if digest(scan_parent["resources"][-1]) != change["resourceSha256"]:
            raise ValueError("SQL auditingSettings projection changed the policy.")
        scan_parent["resources"].pop()
        if "resources" not in parent:
            del scan_parent["resources"]
    if digest(restored) != digest(resource_list(document)):
        raise ValueError("Scan normalization changed unapproved properties or resource coverage.")


def templates(document, path=()):
    resources = document.get("resources")
    if not isinstance(resources, (dict, list)):
        raise ValueError(f"Template has no supported resource collection: {path}")
    yield path, document
    entries = resources.items() if isinstance(resources, dict) else enumerate(resources)
    for symbol, resource in entries:
        if not isinstance(resource, dict) or not isinstance(resource.get("type"), str):
            raise ValueError(f"Malformed resource at {path + (str(symbol),)}")
        if resource["type"].lower() == "microsoft.resources/deployments":
            nested = resource.get("properties", {}).get("template")
            if nested is None:
                raise ValueError("Linked templates are not included in the offline scan.")
            yield from templates(nested, path + (str(symbol),))


def resource_list(document):
    result = copy.deepcopy(document)
    if isinstance(result["resources"], dict):
        result["resources"] = list(result["resources"].values())
    for resource in result["resources"]:
        if resource["type"].lower() == "microsoft.resources/deployments":
            resource["properties"]["template"] = resource_list(resource["properties"]["template"])
    return result


DHCP_EXPRESSION = re.compile(
    r"\[if\(parameters\('(?P<condition>[A-Za-z][A-Za-z0-9_]*)'\), "
    r"createObject\('dnsServers', createArray\(parameters\('(?P<dns>[A-Za-z][A-Za-z0-9_]*)'\)\)\), "
    r"createObject\(\)\)\]"
)


def dhcp_sites(template, path=()):
    for index, resource in enumerate(template["resources"]):
        resource_path = path + ("resources", index)
        properties = resource.get("properties", {})
        if resource["type"].lower() == "microsoft.network/virtualnetworks":
            expression = properties.get("dhcpOptions")
            if isinstance(expression, str):
                match = DHCP_EXPRESSION.fullmatch(expression)
                parameters = template.get("parameters", {})
                if (match is None or parameters.get(match["condition"], {}).get("type") != "bool"
                        or parameters.get(match["dns"], {}).get("type") != "string"):
                    raise ValueError("Unsupported DHCP object expression; no partial or default-only scan.")
                yield {"path": list(resource_path + ("properties", "dhcpOptions")),
                       "scope": list(path), "conditionParameter": match["condition"],
                       "dnsParameter": match["dns"], "sourceExpressionSha256": digest(expression)}
        elif resource["type"].lower() == "microsoft.resources/deployments":
            yield from dhcp_sites(properties["template"], resource_path + ("properties", "template"))


def at_path(document, path):
    value = document
    for key in path:
        value = value[key]
    return value


def verify_dhcp_projection(source, projected, bindings):
    sites = list(dhcp_sites(source))
    if len(sites) != len(bindings):
        raise ValueError("DHCP projection omitted or added a conditional site.")
    restored = copy.deepcopy(projected)
    assignments = {}
    for site, binding in zip(sites, bindings):
        if any(binding.get(key) != value for key, value in site.items()) or type(binding.get("branch")) is not bool:
            raise ValueError("DHCP branch is not bound to its exact source expression.")
        condition = (tuple(site["scope"]), site["conditionParameter"])
        if condition in assignments and assignments[condition] != binding["branch"]:
            raise ValueError("One parameter cannot take conflicting values in the same template scope.")
        assignments[condition] = binding["branch"]
        expected = {"dnsServers": ["[parameters('" + site["dnsParameter"] + "')]"]} if binding["branch"] else {}
        if digest(at_path(projected, site["path"])) != digest(expected):
            raise ValueError("DHCP branch changed the source-derived DNS expression.")
        parent = at_path(restored, site["path"][:-1])
        parent[site["path"][-1]] = at_path(source, site["path"])
    if digest(restored) != digest(source):
        raise ValueError("DHCP projection changed another property, resource, condition or copy.")


def dhcp_variants(template):
    sites = list(dhcp_sites(template))
    conditions = list(dict.fromkeys((tuple(site["scope"]), site["conditionParameter"]) for site in sites))
    if len(conditions) > 8:
        raise ValueError("Too many DHCP branch combinations; explicit scanner support is required.")
    for values in itertools.product((False, True), repeat=len(conditions)):
        assignments = dict(zip(conditions, values))
        projected = copy.deepcopy(template)
        bindings = []
        for site in sites:
            branch = assignments[(tuple(site["scope"]), site["conditionParameter"])]
            value = {"dnsServers": ["[parameters('" + site["dnsParameter"] + "')]"]} if branch else {}
            at_path(projected, site["path"][:-1])[site["path"][-1]] = value
            bindings.append({**site, "branch": branch})
        verify_dhcp_projection(template, projected, bindings)
        yield projected, bindings


def export(source, destination):
    manifest_path = destination.with_suffix(".manifest.json")
    original_path = destination.with_suffix(".original.json")
    if destination.exists() or manifest_path.exists() or original_path.exists():
        raise ValueError("Use a new, empty scan-input directory; stale templates must not be scanned.")
    source_bytes = source.read_bytes()
    source_text = source_bytes.decode("utf-8")
    # Checkov 3.4.5 discards failed ARM checks containing these unresolved functions.
    for expression in ("toLower(", "trim(", "join(", "split(", "substring("):
        if expression in source_text:
            raise ValueError(f"Checkov may silently filter unresolved {expression} expressions; review scanner support first.")
    document = json.loads(source_text)
    inventory = list(templates(document))
    if not inventory:
        raise ValueError("No templates found.")
    normalized, changes = scan_copy(document)
    projected = list(templates(normalized))
    if len(inventory) != len(projected):
        raise ValueError("Scan normalization changed template coverage.")
    manifest = []
    files = []
    for index, ((path, template), (_, scan_template)) in enumerate(zip(inventory, projected)):
        resources = template["resources"]
        entries = list(resources.items() if isinstance(resources, dict) else enumerate(resources))
        variants = list(dhcp_variants(scan_template))
        for variant_index, (variant, bindings) in enumerate(variants):
            suffix = f"-DHCP-{variant_index:03}" if bindings else ""
            filename = f"SCAN-ONLY-{index:03}{suffix}.json"
            contents = (json.dumps(variant, indent=2, ensure_ascii=True) + "\n").encode("utf-8")
            manifest.append({
                "file": filename,
                "templatePath": list(path),
                "scanSha256": hashlib.sha256(contents).hexdigest(),
                "preDhcpScanSha256": digest(scan_template),
                "dhcpBranchBindings": bindings,
                "resources": [
                    {"symbol": str(symbol), "type": resource["type"], "existing": resource.get("existing", False),
                     "sourceSha256": digest(resource)}
                    for symbol, resource in entries
                ],
            })
            files.append((filename, contents))
    # Do not publish partial scan input after a failed relationship/coverage proof.
    destination.mkdir(parents=True)
    for filename, contents in files:
        (destination / filename).write_bytes(contents)
    original_path.write_bytes(source_bytes)
    manifest_path.write_text(json.dumps({
        "sourceSha256": hashlib.sha256(source_bytes).hexdigest(),
        "originalFile": original_path.name,
        "sourceTemplateCount": len(inventory),
        "scanVariantCount": len(manifest),
        "normalizations": changes,
        "templates": manifest,
    }, indent=2) + "\n", encoding="utf-8")
    print(f"Exported all {len(inventory)} compiled templates in {len(manifest)} exhaustive scan views; manifest: {manifest_path}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("destination", type=Path)
    options = parser.parse_args()
    export(options.source, options.destination)
